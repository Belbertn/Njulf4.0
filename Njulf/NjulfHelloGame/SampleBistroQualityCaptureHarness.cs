using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Core.Camera;
using Njulf.Core.Math;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;
using NumericsVector3 = System.Numerics.Vector3;

namespace NjulfHelloGame;

public enum SampleBistroQualityCaptureVariant : byte
{
    Presentation,
    SteadyMotion,
    SunScaleStep,
    SunDirectionStep,
    HybridRayQueryAb
}

public sealed record SampleBistroQualityCameraBookmark(
    string Name,
    Vector3 Position,
    float Yaw,
    float Pitch,
    float FieldOfView,
    float NearPlane,
    float FarPlane);

public sealed record SampleBistroQualityFrameState(
    int AbsoluteFrameIndex,
    int LoopFrameIndex,
    SampleBistroQualityCameraBookmark Camera,
    float DirectionalLightScale,
    float DirectionalLightYawOffsetRadians,
    bool HybridRayQueryEnabled,
    bool LightingEventActive)
{
    public TransparencyMode TransparencyMode { get; init; } =
        TransparencyMode.SortedAlphaBlend;
}

/// <summary>
/// Deterministic camera and lighting contract used by Bistro quality captures
/// and performance runs. The motion path is continuous at the loop boundary,
/// moves on every discrete frame, and crosses multiple primary DDGI cells in
/// both directions without introducing camera cuts.
/// </summary>
public sealed class SampleBistroQualityCaptureContract
{
    public const string Schema = "bistro-quality-run/v11";
    public const int Width = 1920;
    public const int Height = 1080;
    public const int FramesPerSecond = 60;
    public const int LoopFrameCount = 240;
    public const int LightingEventStartFrame = 60;
    public const int LightingEventEndFrame = 180;
    public const int SchedulerFeedbackTransitionGraceFrames = 2;
    public const int WarmupLoopCount = 2;
    public const int CaptureLoopCount = 1;
    public const int TotalCaptureFrameCount =
        (WarmupLoopCount + CaptureLoopCount) * LoopFrameCount;
    public const int FirstMeasuredFrame = WarmupLoopCount * LoopFrameCount;

    // DdgiHigh resolves the primary Bistro camera ring to 0.875 m spacing.
    // The near-ring hysteresis is roughly six metres, so a fourteen-metre
    // closed loop is deliberate: it proves scrolling rather than merely
    // moving within one stable anchor.
    public const float PrimaryProbeSpacing = 0.875f;
    public const float MotionForwardRadius = 6.5f;
    public const float MotionLateralRadius = 0.5f;
    public const float SteppedDirectionalLightScale = 0.5f;
    public const float SteppedDirectionDegrees = 10.0f;

    public static SampleBistroQualityCaptureContract Default { get; } =
        new(SampleBistroQualityCaptureVariant.SunScaleStep);

    public SampleBistroQualityCaptureContract(
        SampleBistroQualityCaptureVariant variant)
    {
        if (!Enum.IsDefined(variant))
            throw new ArgumentOutOfRangeException(nameof(variant));

        Variant = variant;
        CameraPathFingerprint = CreateFingerprint(
            "camera",
            ReferenceBeautyBookmark.Name,
            ReferenceBeautyBookmark.Position.X,
            ReferenceBeautyBookmark.Position.Y,
            ReferenceBeautyBookmark.Position.Z,
            ReferenceBeautyBookmark.Yaw,
            ReferenceBeautyBookmark.Pitch,
            ReferenceBeautyBookmark.FieldOfView,
            PrimaryProbeSpacing,
            MotionCenterBookmark.Position.X,
            MotionCenterBookmark.Position.Y,
            MotionCenterBookmark.Position.Z,
            MotionForwardRadius,
            MotionLateralRadius,
            LoopFrameCount);
        LightingScriptFingerprint = CreateFingerprint(
            "lighting",
            variant,
            LightingEventStartFrame,
            LightingEventEndFrame,
            FirstMeasuredFrame,
            SteppedDirectionalLightScale,
            SteppedDirectionDegrees,
            LoopFrameCount);
        Fingerprint = CreateFingerprint(
            Schema,
            CameraPathFingerprint,
            LightingScriptFingerprint,
            Width,
            Height,
            FramesPerSecond);
    }

    public SampleBistroQualityCaptureVariant Variant { get; }

    /// <summary>
    /// Locked production bookmark. It intentionally remains separate from the
    /// canonical Bistro performance camera so visual framing can evolve without
    /// invalidating the historic fixed-camera timing baseline.
    /// </summary>
    public SampleBistroQualityCameraBookmark ReferenceBeautyBookmark { get; } =
        new(
            "bistro-reference-beauty",
            new Vector3(-15.5f, 2.65f, 1.24f),
            1.89f,
            -0.145f,
            MathF.PI / 3.2f,
            0.05f,
            500.0f);

    /// <summary>
    /// Exact camera from the 2026-08-27 flat/material-appearance incident.
    /// It is intentionally separate from the authored beauty bookmark so
    /// renderer and asset-path A/B captures reproduce the reported pixels.
    /// </summary>
    public static SampleBistroQualityCameraBookmark SnapshotIncidentBookmark
    {
        get;
    } = new(
        "BistroMaterialAppearanceIncident20260827",
        new Vector3(-17.155024f, 2.2722917f, -0.5056352f),
        1.7253896f,
        -0.12267089f,
        0.98174775f,
        0.05f,
        500.0f);

    /// <summary>
    /// Exact camera from the 2026-09-01 Bistro foliage-opacity incident.
    /// It remains independent of the beauty and prior incident bookmarks so
    /// foliage fixes can be compared against the originally reported pixels.
    /// </summary>
    public static SampleBistroQualityCameraBookmark FoliageIncidentBookmark
    {
        get;
    } = new(
        "BistroFoliageOpacityIncident20260901",
        new Vector3(-5.6780605f, 2.5552828f, 1.6660455f),
        1.6427298f,
        0.0660575f,
        0.98174775f,
        0.05f,
        500.0f);

    /// <summary>
    /// The motion route is independent of the presentation bookmark. This
    /// keeps visual-composition iteration from weakening the DDGI stress test.
    /// </summary>
    public SampleBistroQualityCameraBookmark MotionCenterBookmark { get; } =
        new(
            "bistro-motion-center",
            new Vector3(-16.0f, 2.65f, 1.24f),
            MathF.PI * 0.5f,
            -0.04f,
            MathF.PI / 3.0f,
            0.05f,
            500.0f);

    public string CameraPathFingerprint { get; }
    public string LightingScriptFingerprint { get; }
    public string Fingerprint { get; }

    public bool UsesContinuousCameraMotion =>
        Variant != SampleBistroQualityCaptureVariant.Presentation;

    public SampleBistroQualityFrameState ResolveFrame(int absoluteFrameIndex)
    {
        if (absoluteFrameIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(absoluteFrameIndex));

        int loopFrameIndex = absoluteFrameIndex % LoopFrameCount;
        // Warmup exercises the same moving-camera route while holding one
        // lighting state. The bounded relight event belongs to the measured
        // loop; replaying it during warmup would leave a new global source wave
        // only 60 frames before measurement begins.
        bool eventActive = absoluteFrameIndex >= FirstMeasuredFrame &&
            loopFrameIndex >= LightingEventStartFrame &&
            loopFrameIndex < LightingEventEndFrame;
        float lightScale =
            Variant == SampleBistroQualityCaptureVariant.SunScaleStep &&
            eventActive
                ? SteppedDirectionalLightScale
                : 1.0f;
        float directionOffset =
            Variant == SampleBistroQualityCaptureVariant.SunDirectionStep &&
            eventActive
                ? DegreesToRadians(SteppedDirectionDegrees)
                : 0.0f;
        bool hybridRayQueryEnabled =
            Variant == SampleBistroQualityCaptureVariant.HybridRayQueryAb &&
            eventActive;

        return new SampleBistroQualityFrameState(
            absoluteFrameIndex,
            loopFrameIndex,
            ResolveCamera(loopFrameIndex),
            lightScale,
            directionOffset,
            hybridRayQueryEnabled,
            eventActive)
        {
            TransparencyMode =
                Variant == SampleBistroQualityCaptureVariant.HybridRayQueryAb &&
                loopFrameIndex >= LoopFrameCount / 2
                    ? TransparencyMode.WeightedBlendedOit
                    : TransparencyMode.SortedAlphaBlend
        };
    }

    public SampleBistroQualityCameraBookmark ResolveCamera(int loopFrameIndex)
    {
        if ((uint)loopFrameIndex >= LoopFrameCount)
            throw new ArgumentOutOfRangeException(nameof(loopFrameIndex));
        if (!UsesContinuousCameraMotion)
            return ReferenceBeautyBookmark;

        float phase = 2.0f * MathF.PI * loopFrameIndex / LoopFrameCount;
        float sin = MathF.Sin(phase);
        float cos = MathF.Cos(phase);
        Vector3 position = MotionCenterBookmark.Position +
            new Vector3(
                -MotionForwardRadius * cos,
                0.10f * MathF.Sin(phase * 2.0f),
                MotionLateralRadius * sin);

        // Track one stable courtyard point. This creates natural disocclusion
        // while keeping the test legible and avoids synthetic camera cuts.
        var focus = new Vector3(0.5f, 3.8f, 1.5f);
        Vector3 delta = focus - position;
        float length = MathF.Sqrt(
            delta.X * delta.X + delta.Y * delta.Y + delta.Z * delta.Z);
        Vector3 direction = length > 1.0e-6f
            ? delta / length
            : new Vector3(1.0f, 0.0f, 0.0f);
        float cameraYaw = MathF.Atan2(direction.X, -direction.Z);
        float cameraPitch = -MathF.Asin(Math.Clamp(direction.Y, -1.0f, 1.0f));
        return MotionCenterBookmark with
        {
            Name = $"bistro-quality-{loopFrameIndex:D3}",
            Position = position,
            Yaw = cameraYaw,
            Pitch = cameraPitch
        };
    }

    private static float DegreesToRadians(float degrees) =>
        degrees * (MathF.PI / 180.0f);

    private static string CreateFingerprint(params object[] values)
    {
        var canonical = new StringBuilder();
        foreach (object value in values)
        {
            if (canonical.Length > 0)
                canonical.Append('|');
            canonical.Append(value switch
            {
                float single => single.ToString("R", CultureInfo.InvariantCulture),
                double scalar => scalar.ToString("R", CultureInfo.InvariantCulture),
                IFormattable formattable =>
                    formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value?.ToString() ?? string.Empty
            });
        }
        byte[] hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(canonical.ToString()));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed record SampleBistroQualityFrameTelemetry(
    int AbsoluteFrameIndex,
    int LoopFrameIndex,
    bool LightingEventActive,
    float DirectionalLightScale,
    float DirectionalLightYawOffsetRadians,
    bool HybridRayQueryEnabled,
    PerformanceCaptureCameraMetadata Camera,
    float Exposure,
    long CpuFrameMicroseconds,
    long GpuFrameMicroseconds,
    long GpuForwardOpaqueMicroseconds,
    long GpuSimpleDdgiReceiverCacheMicroseconds,
    long GpuSimpleDdgiUpdateMicroseconds,
    long GpuSimpleDdgiTransportAuditMicroseconds,
    long GpuSimpleDdgiUrgentRelightMicroseconds,
    int SimpleDdgiProbesUpdated,
    int SimpleDdgiRecentered,
    int SimpleDdgiAtlasPreservedOnRecenter,
    int SimpleDdgiAtlasCleared,
    ulong SimpleDdgiReceiverInvalidationBytes,
    int SimpleDdgiReceiverInvalidationRangeCount,
    int SimpleDdgiReceiverFullClear,
    uint SimpleDdgiVolumeResourceGeneration,
    uint SimpleDdgiTransportTopologyGeneration,
    SimpleDdgiVolumeRemapKind SimpleDdgiVolumeRemapKind,
    ulong SimpleDdgiCompatibleToroidalScrollCount,
    ulong SimpleDdgiIncompatibleTopologyChangeCount,
    ulong SimpleDdgiGlobalConvergenceRestartCount,
    ulong SimpleDdgiWholeReadbackDropCount,
    int SimpleDdgiSchedulerFullRebuildCount,
    uint SimpleDdgiSourceLightingGeneration,
    uint SimpleDdgiAdmittedSourceCohortGeneration,
    uint SimpleDdgiLivePropagationSourceGeneration,
    uint SimpleDdgiTransportGeneration,
    uint SimpleDdgiPublishedPropagationGeneration,
    int SimpleDdgiLightingDirtyFrames,
    uint SimpleDdgiDirtyReasonFlags,
    int SimpleDdgiTransportSourceRefreshProbeCount,
    int SimpleDdgiTransportSourceStaleProbeCount,
    int SimpleDdgiTransportSourceCohortTransitionActive,
    int SimpleDdgiTransportSourceCohortElapsedFrames,
    int SimpleDdgiTransportGlobalConvergencePending,
    int SimpleDdgiTransportGlobalConvergenceElapsedFrames,
    int SimpleDdgiTransportSourceReadyProbeCount,
    int SimpleDdgiTransportConvergedProbeCount,
    int SimpleDdgiTransportPendingSolverProbeCount,
    bool SimpleDdgiTransportTailCertificationEnabled,
    SimpleDdgiTransportPhase SimpleDdgiTransportTailPhase,
    SimpleDdgiTransportCertificationReason SimpleDdgiTransportTailReason,
    uint SimpleDdgiTransportTailSolveEpoch,
    uint SimpleDdgiTransportTailAuditEpoch,
    uint SimpleDdgiTransportTailExpectedParticipantCount,
    uint SimpleDdgiTransportTailAuditedParticipantCount,
    uint SimpleDdgiTransportTailExcludedStaleSourceCount,
    uint SimpleDdgiTransportTailCacheIdentityFailureCount,
    uint SimpleDdgiTransportTailCacheCardinalityFailureCount,
    uint SimpleDdgiTransportTailCacheSourceGenerationFailureCount,
    uint SimpleDdgiTransportTailCacheSourceEpochFailureCount,
    uint SimpleDdgiTransportTailCachePhysicalGenerationFailureCount,
    uint SimpleDdgiTransportTailNonFiniteCount,
    uint SimpleDdgiTransportTailCounterOverflowCount,
    float SimpleDdgiTransportTailFixedPointDefect,
    float SimpleDdgiTransportTailFieldMagnitude,
    float SimpleDdgiTransportTailConfiguredContractionBound,
    float SimpleDdgiTransportTailObservedContractionBound,
    float SimpleDdgiTransportTailCertifiedContractionBound,
    float SimpleDdgiTransportTailAbsoluteBound,
    float SimpleDdgiTransportTailRelativeBound,
    float SimpleDdgiTransportTailTolerance,
    float SimpleDdgiTransportTailCanonicalQuantizationFloor,
    int SimpleDdgiTransportTailConvergenceDeadlineFrames,
    ulong SimpleDdgiTransportTailConvergenceDeadlineRecoveryCount,
    int SimpleDdgiSchedulerReady,
    int SimpleDdgiSchedulerFeedbackValid,
    ulong SimpleDdgiSchedulerFeedbackFrameSerial,
    uint SimpleDdgiSchedulerFeedbackPendingFreshCount,
    uint SimpleDdgiSchedulerFeedbackPendingExposedCount,
    uint SimpleDdgiSchedulerFeedbackPendingRelocationCount,
    uint SimpleDdgiSchedulerFeedbackPendingSourceCount,
    uint SimpleDdgiSchedulerFeedbackPendingSourceInvalidFlagCount,
    uint SimpleDdgiSchedulerFeedbackPendingSourcePrivateRepairCount,
    uint SimpleDdgiSchedulerFeedbackPendingSourceCardinalityCount,
    uint SimpleDdgiSchedulerFeedbackPendingSourceGenerationCount,
    uint SimpleDdgiSchedulerFeedbackSolveParticipantCount,
    uint SimpleDdgiSchedulerFeedbackSolveVisitedCount,
    uint SimpleDdgiSchedulerFeedbackSolveEpoch,
    uint SimpleDdgiSchedulerFeedbackSourceProbeCount,
    uint SimpleDdgiSchedulerFeedbackTransportRayCount,
    uint SimpleDdgiSchedulerFeedbackCachedSolverProbeCount,
    uint SimpleDdgiSchedulerFeedbackPublishedCount,
    ulong SimpleDdgiSchedulerStaleFeedbackCount,
    ulong SimpleDdgiSchedulerFeedbackGenerationRejectionCount,
    ulong SimpleDdgiSchedulerFallbackCount,
    string SimpleDdgiSchedulerFallbackReason,
    int DirtyFirstUpdateLatencySampleCount,
    int DirtyFirstUpdateLatencyP95Frames,
    int DirtyConvergenceLatencySampleCount,
    int DirtyConvergenceLatencyP95Frames,
    uint SimpleDdgiReceiverContributingProbeCount,
    uint SimpleDdgiReceiverFallbackProbeCount,
    int SimpleDdgiUrgentRelightActive,
    uint SimpleDdgiUrgentRelightAcceptedCount,
    uint SimpleDdgiUrgentRelightCommittedCount,
    uint SimpleDdgiUrgentRelightRejectedCount,
    int ReflectionProbeCount,
    int ReflectionProbeCapturesQueued,
    int ReflectionProbeCapturesCompleted,
    ulong ReflectionProbeCapturesCompletedTotal,
    int ReflectionProbePublishedCount,
    long GpuReflectionProbeCaptureMicroseconds,
    long GpuReflectionProbePrefilterMicroseconds,
    int ScreenshotPendingCount)
{
    /// <summary>Recorded work and planner state for this diagnostics frame.</summary>
    public ReflectionProbeLifecycleFrameSnapshot ReflectionProbeCurrentLifecycle { get; init; }
    public ReflectionProbeGpuBudgetSnapshot ReflectionProbeCurrentCaptureBudget { get; init; }

    /// <summary>
    /// Fence-complete lifecycle aligned with the reflection GPU timings below.
    /// It can name an earlier renderer frame than ReflectionProbeCurrentLifecycle.
    /// </summary>
    public ReflectionProbeLifecycleFrameSnapshot ReflectionProbeCompletedLifecycle { get; init; }
    public long GpuReflectionProbePublishMicroseconds { get; init; }
    public ReflectionImplementationMode RequestedReflectionImplementation
        { get; init; } = ReflectionImplementationMode.Auto;
    public ReflectionImplementationMode EffectiveReflectionImplementation
        { get; init; } = ReflectionImplementationMode.Adaptive;
    public ReflectionImplementationFallbackReason
        ReflectionImplementationFallbackReason { get; init; }
    public string ReflectionImplementationFallbackDetail { get; init; } =
        string.Empty;
    public int HybridReflectionCountersReadbackValid { get; init; }
    public uint HybridReflectionSsrHitCount { get; init; }
    public uint HybridReflectionRayQueryRequestCount { get; init; }
    public uint HybridReflectionRayQueryCount { get; init; }
    public uint HybridReflectionRayQueryOverflowCount { get; init; }
    public uint HybridReflectionRayQueryHitCount { get; init; }
    public uint HybridReflectionRayQueryMissCount { get; init; }
    public uint HybridReflectionDdgiFallbackCount { get; init; }
    public uint HybridReflectionProbeFallbackCount { get; init; }
    public uint HybridReflectionEnvironmentFallbackCount { get; init; }
    public uint HybridReflectionFullRateTileCount { get; init; }
    public uint HybridReflectionHalfRateTileCount { get; init; }
    public uint HybridReflectionQuarterRateTileCount { get; init; }
    public uint HybridReflectionAnalyticTileCount { get; init; }
    public uint HybridReflectionReuseTileCount { get; init; }
    public uint HybridReflectionActiveTileCount { get; init; }
    public uint HybridReflectionTileOverflowCount { get; init; }
    public int AutomaticPlanarReflectionActive { get; init; }
    public int AutomaticPlanarCandidateCount { get; init; }
    public int AutomaticPlanarSelectedCount { get; init; }
    public int AutomaticPlanarCaptureCount { get; init; }
    public int AutomaticPlanarReprojectionCount { get; init; }
    public int AutomaticPlanarRejectedCount { get; init; }
    public AutomaticPlanarCandidateRejectionReason
        AutomaticPlanarRejectionReason { get; init; }
    public uint AutomaticPlanarCaptureGeneration { get; init; }
    public ulong AutomaticPlanarEstimatedBytes { get; init; }
    public float AutomaticPlanarResolutionScale { get; init; }
    public uint AutomaticPlanarMaximumCaptureAge { get; init; }
    public AutomaticPlanarExclusionEncodingMode
        AutomaticPlanarExclusionEncodingMode { get; init; }
    public int AutomaticPlanarBitsetCaptureCount { get; init; }
    public int AutomaticPlanarSortedListFallbackCount { get; init; }
    public AutomaticPlanarMetadataSlotTelemetry[]
        AutomaticPlanarMetadataSlots { get; init; } = [];
    public int AutomaticPlanarMetadataPayloadWordCount { get; init; }
    public int AutomaticPlanarMetadataWordsUsed { get; init; }
    public int AutomaticPlanarMetadataBankHighWaterMark { get; init; }
    public int AutomaticPlanarMetadataCapacityRejectionCount { get; init; }
    public long GpuAutomaticPlanarCaptureMicroseconds { get; init; }
    public long GpuHybridReflectionSsrMicroseconds { get; init; }
    public long GpuHybridReflectionRayQueryMicroseconds { get; init; }
    public long GpuHybridReflectionDdgiBaseMicroseconds { get; init; }
    public long GpuHybridReflectionResolveMicroseconds { get; init; }
    public long GpuHybridReflectionTemporalMicroseconds { get; init; }
    public long GpuHybridReflectionSpatialMicroseconds { get; init; }
    public long GpuHybridReflectionCompositeMicroseconds { get; init; }
    public TransparencyMode TransparencyMode { get; init; } =
        TransparencyMode.SortedAlphaBlend;
    public int TransparentSceneReflectionSsrSampleBudget { get; init; }
    public uint TransparentReflectionRayRequestCount { get; init; }
    public uint TransparentReflectionExactSsrEligibleCount { get; init; }
    public uint TransparentReflectionExactSsrAdmittedCount { get; init; }
    public uint TransparentReflectionExactSsrReservedSampleCount { get; init; }
    public uint TransparentReflectionExactSsrActualSampleCount { get; init; }
    public uint TransparentReflectionExactSsrHitCount { get; init; }
    public uint TransparentReflectionExactSsrBudgetRejectedCount { get; init; }
    public uint TransparentReflectionExactRayAdmittedCount { get; init; }
    public uint TransparentReflectionExactRayBudgetRejectedCount { get; init; }
}

public sealed record SampleBistroQualityGateResult(
    bool Passed,
    bool ProjectionStable,
    bool ExposureStable,
    bool CameraMovedEveryFrame,
    bool CameraCutFree,
    bool WarmupConverged,
    bool SchedulerFeedbackStable,
    bool TailCertified,
    bool TransportTopologyStable,
    bool LogicalVolumeTableScrolled,
    int RecenteredFrameCount,
    int AtlasPreservedFrameCount,
    int AtlasClearedFrameCount,
    int ReceiverMapFullClearFrameCount,
    ulong CompatibleToroidalScrollCount,
    ulong IncompatibleTopologyChangeCount,
    ulong GlobalConvergenceRestartCount,
    ulong WholeReadbackDropCount,
    int SchedulerFullRebuildCount,
    int LightingGenerationResponseFrames,
    int DirtyFirstUpdateLatencyP95Frames,
    int DirtyConvergenceLatencyP95Frames,
    int VisibleRelightProbeTarget,
    int VisibleRelightProbeUpdates,
    int ReflectionProbeCount,
    IReadOnlyList<string> Failures)
{
    public bool HybridReflectionTelemetryValid { get; init; }
    public ulong HybridReflectionDdgiFallbackCount { get; init; }
    public ulong HybridReflectionProbeFallbackCount { get; init; }
    public ulong HybridReflectionEnvironmentFallbackCount { get; init; }
}

public sealed record SampleBistroQualityArtifact(
    string Name,
    string RelativePath,
    long ByteLength,
    string Sha256);

public sealed record SampleBistroQualityRunReport(
    string Kind,
    string Schema,
    DateTimeOffset CapturedAtUtc,
    string Status,
    SampleBistroQualityCaptureVariant Variant,
    string ContractFingerprint,
    string CameraPathFingerprint,
    string LightingScriptFingerprint,
    int Width,
    int Height,
    int FramesPerSecond,
    IReadOnlyList<SampleBistroQualityFrameTelemetry> Frames,
    IReadOnlyList<SampleBistroQualityArtifact> Artifacts,
    SampleBistroQualityGateResult? Gate,
    string Failure);

internal sealed class SampleBistroQualityRuntimeController
{
    private readonly VulkanRenderer _renderer;
    private readonly FirstPersonCamera _camera;
    private readonly LightManager _lightManager;
    private readonly LightHandle _directionalLightHandle;
    private readonly Light _baseDirectionalLight;
    private readonly ReflectionMode _baseReflectionMode;
    private readonly float _baseRayQueryPixelBudgetFraction;
    private readonly TransparencyMode _baseTransparencyMode;
    private readonly bool _baseTransparentSampleReflections;
    private readonly int _baseTransparentRayTaskBudget;
    private readonly int _baseTransparentSsrSampleBudget;
    private readonly bool _baseAutoExposureEnabled;
    private readonly float _baseExposure;
    private SampleBistroQualityFrameState? _lastAppliedState;

    public SampleBistroQualityRuntimeController(
        VulkanRenderer renderer,
        FirstPersonCamera camera,
        LightManager lightManager,
        SampleBistroQualityCaptureContract contract)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _lightManager = lightManager ??
            throw new ArgumentNullException(nameof(lightManager));
        Contract = contract ?? throw new ArgumentNullException(nameof(contract));

        LightRecord directional = lightManager.GetLightRecords()
            .FirstOrDefault(static record =>
                record.Light.Type == LightType.Directional);
        if (!directional.Handle.IsValid)
        {
            throw new InvalidOperationException(
                "The Bistro quality contract requires one directional light.");
        }

        _directionalLightHandle = directional.Handle;
        _baseDirectionalLight = directional.Light;
        _baseReflectionMode = renderer.Settings.Reflections.Mode;
        _baseRayQueryPixelBudgetFraction =
            renderer.Settings.Reflections.RayQueryPixelBudgetFraction;
        _baseTransparencyMode = renderer.Settings.Transparency.Mode;
        _baseTransparentSampleReflections =
            renderer.Settings.Transparency.SampleReflections;
        _baseTransparentRayTaskBudget =
            renderer.Settings.Transparency.SceneReflectionRayTaskBudget;
        _baseTransparentSsrSampleBudget =
            renderer.Settings.Transparency.SceneReflectionSsrSampleBudget;
        _baseAutoExposureEnabled = renderer.Settings.AutoExposure.Enabled;
        _baseExposure = renderer.Settings.Exposure;
    }

    public SampleBistroQualityCaptureContract Contract { get; }
    public SampleBistroQualityFrameState? LastAppliedState => _lastAppliedState;

    public SampleBistroQualityFrameState PrepareFrame(int absoluteFrameIndex)
    {
        SampleBistroQualityFrameState state =
            Contract.ResolveFrame(absoluteFrameIndex);
        ApplyCamera(state.Camera);
        ApplyLighting(state);
        _lastAppliedState = state;
        return state;
    }

    public void Restore()
    {
        _lightManager.UpdateLight(
            _directionalLightHandle,
            _baseDirectionalLight);
        _renderer.Settings.Reflections.Mode = _baseReflectionMode;
        _renderer.Settings.Reflections.RayQueryPixelBudgetFraction =
            _baseRayQueryPixelBudgetFraction;
        _renderer.Settings.Transparency.Mode = _baseTransparencyMode;
        _renderer.Settings.Transparency.SampleReflections =
            _baseTransparentSampleReflections;
        _renderer.Settings.Transparency.SceneReflectionRayTaskBudget =
            _baseTransparentRayTaskBudget;
        _renderer.Settings.Transparency.SceneReflectionSsrSampleBudget =
            _baseTransparentSsrSampleBudget;
        _renderer.Settings.Exposure = _baseExposure;
        _renderer.Settings.AutoExposure.Enabled = _baseAutoExposureEnabled;
    }

    public void LockExposure(float exposure)
    {
        if (!float.IsFinite(exposure) || exposure <= 0.0f)
            exposure = _baseExposure;
        _renderer.Settings.Exposure = Math.Clamp(
            exposure,
            _renderer.Settings.AutoExposure.MinExposure,
            _renderer.Settings.AutoExposure.MaxExposure);
        _renderer.Settings.AutoExposure.Enabled = false;
    }

    private void ApplyCamera(SampleBistroQualityCameraBookmark bookmark)
    {
        _camera.Position = bookmark.Position;
        _camera.Yaw = bookmark.Yaw;
        _camera.Pitch = bookmark.Pitch;
        _camera.FieldOfView = bookmark.FieldOfView;
        _camera.NearPlane = bookmark.NearPlane;
        _camera.FarPlane = bookmark.FarPlane;
        _camera.AspectRatio =
            SampleBistroQualityCaptureContract.Width /
            (float)SampleBistroQualityCaptureContract.Height;
        _camera.Update();
    }

    private void ApplyLighting(SampleBistroQualityFrameState state)
    {
        var light = _baseDirectionalLight;
        light.Intensity = _baseDirectionalLight.Intensity *
            state.DirectionalLightScale;
        light.Direction = RotateAroundWorldY(
            _baseDirectionalLight.Direction,
            state.DirectionalLightYawOffsetRadians);

        if (!_lightManager.TryGetLight(
                _directionalLightHandle,
                out Light current) ||
            !current.Equals(light))
        {
            _lightManager.UpdateLight(_directionalLightHandle, light);
        }

        if (Contract.Variant ==
            SampleBistroQualityCaptureVariant.HybridRayQueryAb)
        {
            // DDGI remains the reflection base in both halves. The A/B event
            // changes only the bounded sharp-detail recovery budget.
            _renderer.Settings.Reflections.Mode = ReflectionMode.HybridRayQuery;
            _renderer.Settings.Reflections.RayQueryPixelBudgetFraction =
                state.HybridRayQueryEnabled
                    ? _baseRayQueryPixelBudgetFraction
                    : 0.0f;
            _renderer.Settings.Transparency.Mode = state.TransparencyMode;
            _renderer.Settings.Transparency.SampleReflections = true;
            _renderer.Settings.Transparency.SceneReflectionRayTaskBudget =
                state.HybridRayQueryEnabled
                    ? Math.Max(_baseTransparentRayTaskBudget, 65_536)
                    : 0;
            _renderer.Settings.Transparency.SceneReflectionSsrSampleBudget =
                Math.Max(_baseTransparentSsrSampleBudget, 4_194_304);
        }
    }

    private static NumericsVector3 RotateAroundWorldY(
        NumericsVector3 direction,
        float angle)
    {
        if (MathF.Abs(angle) <= float.Epsilon)
            return direction;
        float sin = MathF.Sin(angle);
        float cos = MathF.Cos(angle);
        return NumericsVector3.Normalize(new NumericsVector3(
            direction.X * cos + direction.Z * sin,
            direction.Y,
            -direction.X * sin + direction.Z * cos));
    }
}

internal sealed class SampleBistroQualityCaptureRunner
{
    private const int ScreenshotCompletionTimeoutFrames = 180;
    private static readonly int[] DynamicCapturePhases =
        [0, 59, 60, 61, 68, 76, 179, 180, 181, 239];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly VulkanRenderer _renderer;
    private readonly SampleBistroQualityRuntimeController _controller;
    private readonly string _outputDirectory;
    private readonly Action _exit;
    private readonly List<SampleBistroQualityFrameTelemetry> _frames = [];
    private readonly Dictionary<string, string> _requestedArtifacts =
        new(StringComparer.Ordinal);
    private int _completionWaitFrames;
    private bool _completed;
    private bool _renderDocCaptureAttempted;

    public SampleBistroQualityCaptureRunner(
        VulkanRenderer renderer,
        SampleBistroQualityRuntimeController controller,
        string outputDirectory,
        Action exit)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _controller = controller ??
            throw new ArgumentNullException(nameof(controller));
        _outputDirectory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(outputDirectory)
                ? throw new ArgumentException(
                    "A Bistro quality output directory is required.",
                    nameof(outputDirectory))
                : outputDirectory);
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));

        renderer.Settings.Debug.Enabled = true;
        renderer.Settings.Debug.AllowScreenshots = true;
        renderer.Settings.Debug.AllowGpuTiming = true;
        WriteReport("running", string.Empty);
    }

    /// <summary>
    /// The deterministic quality clock must not consume bootstrap clears or an
    /// exact fallback frame when the run explicitly requests a cache path. A
    /// cold driver or RenderDoc injection can make post-present compilation
    /// much slower than the ordinary 720-frame script.
    /// </summary>
    internal static bool IsReadyForCapture(
        bool fullQualityPresented,
        SimpleDdgiReceiverCacheMode configuredMode,
        in SimpleDdgiReceiverCacheDiagnostics diagnostics)
    {
        if (!fullQualityPresented)
            return false;

        SimpleDdgiReceiverCacheMode requested = configuredMode.Sanitize();
        if (!requested.UsesCache())
        {
            return diagnostics.RequestedMode == requested &&
                   diagnostics.EffectiveMode ==
                       SimpleDdgiReceiverCacheMode.Exact;
        }

        return diagnostics.RequestedMode == requested &&
               diagnostics.EffectiveMode == requested &&
               diagnostics.FallbackReason ==
                   SimpleDdgiReceiverCacheFallbackReason.None;
    }

    public void PrepareFrame(int absoluteFrameIndex)
    {
        if (_completed)
            return;

        SampleBistroQualityFrameState state =
            _controller.PrepareFrame(absoluteFrameIndex);
        if (absoluteFrameIndex <
            SampleBistroQualityCaptureContract.WarmupLoopCount *
            SampleBistroQualityCaptureContract.LoopFrameCount)
        {
            return;
        }
        if (absoluteFrameIndex >=
            SampleBistroQualityCaptureContract.TotalCaptureFrameCount)
        {
            return;
        }

        // When the harness is launched through RenderDoc, capture the first
        // relight frame automatically. Normal runs remain unchanged because
        // the renderer's integration fails closed when renderdoc.dll is not
        // injected.
        if (!_renderDocCaptureAttempted &&
            _controller.Contract.UsesContinuousCameraMotion &&
            state.LoopFrameIndex ==
                SampleBistroQualityCaptureContract.LightingEventStartFrame)
        {
            _renderDocCaptureAttempted = true;
            _renderer.Settings.Debug.AllowRenderDocCapture = true;
            _renderer.RequestRenderDocCapture();
        }

        bool shouldCapture =
            _controller.Contract.Variant ==
                SampleBistroQualityCaptureVariant.Presentation
                ? state.LoopFrameIndex == 0
                : DynamicCapturePhases.Contains(state.LoopFrameIndex);
        if (!shouldCapture)
            return;

        string name = $"{state.LoopFrameIndex:D3}-beauty";
        if (_requestedArtifacts.ContainsKey(name))
            return;

        string relativePath = Path.Combine(
            "frames",
            $"{name}.renderer.png");
        string fullPath = Path.Combine(_outputDirectory, relativePath);
        Directory.CreateDirectory(
            Path.GetDirectoryName(fullPath) ?? _outputDirectory);
        _renderer.RequestScreenshot(fullPath);
        _requestedArtifacts.Add(name, relativePath);
    }

    public void OnFrameRendered(
        int absoluteFrameIndex,
        RendererDiagnostics diagnostics)
    {
        if (_completed)
            return;
        ArgumentNullException.ThrowIfNull(diagnostics);

        int firstCaptureFrame =
            SampleBistroQualityCaptureContract.WarmupLoopCount *
            SampleBistroQualityCaptureContract.LoopFrameCount;
        if (absoluteFrameIndex == firstCaptureFrame - 1)
        {
            // Let the authored meter settle for two complete camera loops, then
            // freeze its result. This keeps image comparisons sensitive to DDGI
            // stability instead of camera-dependent exposure adaptation.
            _controller.LockExposure(diagnostics.Exposure);
        }
        if (absoluteFrameIndex >= firstCaptureFrame &&
            absoluteFrameIndex <
                SampleBistroQualityCaptureContract.TotalCaptureFrameCount)
        {
            SampleBistroQualityFrameState state =
                _controller.LastAppliedState ??
                _controller.Contract.ResolveFrame(absoluteFrameIndex);
            SampleBistroQualityFrameTelemetry frame = new(
                absoluteFrameIndex,
                state.LoopFrameIndex,
                state.LightingEventActive,
                state.DirectionalLightScale,
                state.DirectionalLightYawOffsetRadians,
                state.HybridRayQueryEnabled,
                diagnostics.CaptureCamera,
                diagnostics.Exposure,
                diagnostics.CpuTotalDrawSceneMicroseconds,
                diagnostics.GpuFrameMicroseconds,
                diagnostics.GpuForwardOpaqueMicroseconds,
                diagnostics.GpuSimpleDdgiReceiverCacheMicroseconds,
                diagnostics.GpuDdgiUpdateMicroseconds,
                diagnostics.GpuSimpleDdgiTransportAuditMicroseconds,
                diagnostics.GpuSimpleDdgiUrgentRelightMicroseconds,
                diagnostics.SimpleDdgiProbesUpdated,
                diagnostics.SimpleDdgiRecentered,
                diagnostics.SimpleDdgiAtlasPreservedOnRecenter,
                diagnostics.SimpleDdgiAtlasCleared,
                diagnostics.SimpleDdgiReceiverInvalidationBytes,
                diagnostics.SimpleDdgiReceiverInvalidationRangeCount,
                diagnostics.SimpleDdgiReceiverFullClear,
                diagnostics.SimpleDdgiVolumeResourceGeneration,
                diagnostics.SimpleDdgiTransportTopologyGeneration,
                diagnostics.SimpleDdgiVolumeRemapKind,
                diagnostics.SimpleDdgiCompatibleToroidalScrollCount,
                diagnostics.SimpleDdgiIncompatibleTopologyChangeCount,
                diagnostics.SimpleDdgiGlobalConvergenceRestartCount,
                diagnostics.SimpleDdgiWholeReadbackDropCount,
                diagnostics.SimpleDdgiUploadTiming.SchedulerFullRebuildCount,
                diagnostics.SimpleDdgiSourceLightingGeneration,
                diagnostics.SimpleDdgiAdmittedSourceCohortGeneration,
                diagnostics.SimpleDdgiLivePropagationSourceGeneration,
                diagnostics.SimpleDdgiTransportGeneration,
                diagnostics.SimpleDdgiPublishedPropagationGeneration,
                diagnostics.SimpleDdgiLightingDirtyFrames,
                diagnostics.SimpleDdgiDirtyReasonFlags,
                diagnostics.SimpleDdgiTransportSourceRefreshProbeCount,
                diagnostics.SimpleDdgiTransportSourceStaleProbeCount,
                diagnostics.SimpleDdgiTransportSourceCohortTransitionActive,
                diagnostics.SimpleDdgiTransportSourceCohortElapsedFrames,
                diagnostics.SimpleDdgiTransportGlobalConvergencePending,
                diagnostics.SimpleDdgiTransportGlobalConvergenceElapsedFrames,
                diagnostics.SimpleDdgiTransportSourceReadyProbeCount,
                diagnostics.SimpleDdgiTransportConvergedProbeCount,
                diagnostics.SimpleDdgiTransportPendingSolverProbeCount,
                diagnostics.SimpleDdgiTransportTailCertificationEnabled,
                diagnostics.SimpleDdgiTransportConvergence.TailPhase,
                diagnostics.SimpleDdgiTransportConvergence.TailReason,
                diagnostics.SimpleDdgiTransportConvergence.TailSolveEpoch,
                diagnostics.SimpleDdgiTransportConvergence.TailAuditEpoch,
                diagnostics.SimpleDdgiTransportConvergence
                    .TailExpectedParticipantCount,
                diagnostics.SimpleDdgiTransportConvergence
                    .TailAuditedParticipantCount,
                diagnostics.SimpleDdgiTransportConvergence
                    .TailExcludedStaleSourceCount,
                diagnostics.SimpleDdgiTransportConvergence
                    .TailCacheIdentityFailureCount,
                diagnostics.SimpleDdgiTransportConvergence
                    .TailCacheCardinalityFailureCount,
                diagnostics.SimpleDdgiTransportConvergence
                    .TailCacheSourceGenerationFailureCount,
                diagnostics.SimpleDdgiTransportConvergence
                    .TailCacheSourceEpochFailureCount,
                diagnostics.SimpleDdgiTransportConvergence
                    .TailCachePhysicalGenerationFailureCount,
                diagnostics.SimpleDdgiTransportConvergence.TailNonFiniteCount,
                diagnostics.SimpleDdgiTransportConvergence
                    .TailCounterOverflowCount,
                diagnostics.SimpleDdgiTransportConvergence.TailFixedPointDefect,
                diagnostics.SimpleDdgiTransportConvergence.TailFieldMagnitude,
                diagnostics.SimpleDdgiTransportConvergence
                    .TailConfiguredContractionBound,
                diagnostics.SimpleDdgiTransportConvergence
                    .TailObservedContractionBound,
                diagnostics.SimpleDdgiTransportConvergence
                    .TailCertifiedContractionBound,
                diagnostics.SimpleDdgiTransportConvergence.TailAbsoluteBound,
                diagnostics.SimpleDdgiTransportConvergence.TailRelativeBound,
                diagnostics.SimpleDdgiTransportConvergence.TailTolerance,
                diagnostics.SimpleDdgiTransportConvergence
                    .TailCanonicalQuantizationFloor,
                diagnostics.SimpleDdgiTransportConvergence
                    .TailConvergenceDeadlineFrames,
                diagnostics.SimpleDdgiTransportConvergence
                    .TailConvergenceDeadlineRecoveryCount,
                diagnostics.SimpleDdgiSchedulerReady,
                diagnostics.SimpleDdgiSchedulerFeedbackValid,
                diagnostics.SimpleDdgiSchedulerFeedbackFrameSerial,
                diagnostics.SimpleDdgiSchedulerFeedbackPendingFreshCount,
                diagnostics.SimpleDdgiSchedulerFeedbackPendingExposedCount,
                diagnostics.SimpleDdgiSchedulerFeedbackPendingRelocationCount,
                diagnostics.SimpleDdgiSchedulerFeedbackPendingSourceCount,
                diagnostics.SimpleDdgiSchedulerFeedbackPendingSourceInvalidFlagCount,
                diagnostics.SimpleDdgiSchedulerFeedbackPendingSourcePrivateRepairCount,
                diagnostics.SimpleDdgiSchedulerFeedbackPendingSourceCardinalityCount,
                diagnostics.SimpleDdgiSchedulerFeedbackPendingSourceGenerationCount,
                diagnostics.SimpleDdgiSchedulerFeedbackSolveParticipantCount,
                diagnostics.SimpleDdgiSchedulerFeedbackSolveVisitedCount,
                diagnostics.SimpleDdgiSchedulerFeedbackSolveEpoch,
                diagnostics.SimpleDdgiSchedulerFeedbackSourceProbeCount,
                diagnostics.SimpleDdgiSchedulerFeedbackTransportRayCount,
                diagnostics.SimpleDdgiSchedulerFeedbackCachedSolverProbeCount,
                diagnostics.SimpleDdgiSchedulerFeedbackPublishedCount,
                diagnostics.SimpleDdgiSchedulerStaleFeedbackCount,
                diagnostics.SimpleDdgiSchedulerFeedbackGenerationRejectionCount,
                diagnostics.SimpleDdgiSchedulerFallbackCount,
                diagnostics.SimpleDdgiSchedulerFallbackReason,
                diagnostics.SimpleDdgiDirtyFirstUpdateLatencySampleCount,
                diagnostics.SimpleDdgiDirtyFirstUpdateLatencyP95Frames,
                diagnostics.SimpleDdgiDirtyConvergenceLatencySampleCount,
                diagnostics.SimpleDdgiDirtyConvergenceLatencyP95Frames,
                diagnostics.SimpleDdgiReceiverContributingProbeCount,
                diagnostics.SimpleDdgiReceiverFallbackProbeCount,
                diagnostics.SimpleDdgiUrgentRelightActive,
                diagnostics.SimpleDdgiUrgentRelightAcceptedCount,
                diagnostics.SimpleDdgiUrgentRelightCommittedCount,
                diagnostics.SimpleDdgiUrgentRelightRejectedCount,
                diagnostics.ReflectionProbeCount,
                diagnostics.ReflectionProbeCapturesQueued,
                diagnostics.ReflectionProbeCapturesCompleted,
                diagnostics.ReflectionProbeCapturesCompletedTotal,
                diagnostics.ReflectionProbePublishedCount,
                diagnostics.GpuReflectionProbeCaptureMicroseconds,
                diagnostics.GpuReflectionProbePrefilterMicroseconds,
                diagnostics.ScreenshotPendingCount)
            {
                ReflectionProbeCurrentLifecycle =
                    diagnostics.ReflectionProbeCurrentLifecycle,
                ReflectionProbeCurrentCaptureBudget =
                    diagnostics.ReflectionProbeCurrentCaptureBudget,
                ReflectionProbeCompletedLifecycle =
                    diagnostics.ReflectionProbeCompletedLifecycle,
                GpuReflectionProbePublishMicroseconds =
                    diagnostics.GpuReflectionProbePublishMicroseconds,
                RequestedReflectionImplementation =
                    diagnostics.RequestedReflectionImplementation,
                EffectiveReflectionImplementation =
                    diagnostics.EffectiveReflectionImplementation,
                ReflectionImplementationFallbackReason =
                    diagnostics.ReflectionImplementationFallbackReason,
                ReflectionImplementationFallbackDetail =
                    diagnostics.ReflectionImplementationFallbackDetail,
                HybridReflectionCountersReadbackValid =
                    diagnostics.HybridReflectionCountersReadbackValid,
                HybridReflectionSsrHitCount =
                    diagnostics.HybridReflectionSsrHitCount,
                HybridReflectionRayQueryRequestCount =
                    diagnostics.HybridReflectionRayQueryRequestCount,
                HybridReflectionRayQueryCount =
                    diagnostics.HybridReflectionRayQueryCount,
                HybridReflectionRayQueryOverflowCount =
                    diagnostics.HybridReflectionRayQueryOverflowCount,
                HybridReflectionRayQueryHitCount =
                    diagnostics.HybridReflectionRayQueryHitCount,
                HybridReflectionRayQueryMissCount =
                    diagnostics.HybridReflectionRayQueryMissCount,
                HybridReflectionDdgiFallbackCount =
                    diagnostics.HybridReflectionDdgiFallbackCount,
                HybridReflectionProbeFallbackCount =
                    diagnostics.HybridReflectionProbeFallbackCount,
                HybridReflectionEnvironmentFallbackCount =
                    diagnostics.HybridReflectionEnvironmentFallbackCount,
                HybridReflectionFullRateTileCount =
                    diagnostics.HybridReflectionFullRateTileCount,
                HybridReflectionHalfRateTileCount =
                    diagnostics.HybridReflectionHalfRateTileCount,
                HybridReflectionQuarterRateTileCount =
                    diagnostics.HybridReflectionQuarterRateTileCount,
                HybridReflectionAnalyticTileCount =
                    diagnostics.HybridReflectionAnalyticTileCount,
                HybridReflectionReuseTileCount =
                    diagnostics.HybridReflectionReuseTileCount,
                HybridReflectionActiveTileCount =
                    diagnostics.HybridReflectionActiveTileCount,
                HybridReflectionTileOverflowCount =
                    diagnostics.HybridReflectionTileOverflowCount,
                AutomaticPlanarReflectionActive =
                    diagnostics.AutomaticPlanarReflectionActive,
                AutomaticPlanarCandidateCount =
                    diagnostics.AutomaticPlanarCandidateCount,
                AutomaticPlanarSelectedCount =
                    diagnostics.AutomaticPlanarSelectedCount,
                AutomaticPlanarCaptureCount =
                    diagnostics.AutomaticPlanarCaptureCount,
                AutomaticPlanarReprojectionCount =
                    diagnostics.AutomaticPlanarReprojectionCount,
                AutomaticPlanarRejectedCount =
                    diagnostics.AutomaticPlanarRejectedCount,
                AutomaticPlanarRejectionReason =
                    diagnostics.AutomaticPlanarRejectionReason,
                AutomaticPlanarCaptureGeneration =
                    diagnostics.AutomaticPlanarCaptureGeneration,
                AutomaticPlanarEstimatedBytes =
                    diagnostics.AutomaticPlanarEstimatedBytes,
                AutomaticPlanarResolutionScale =
                    diagnostics.AutomaticPlanarResolutionScale,
                AutomaticPlanarMaximumCaptureAge =
                    diagnostics.AutomaticPlanarMaximumCaptureAge,
                AutomaticPlanarExclusionEncodingMode =
                    diagnostics.AutomaticPlanarExclusionEncodingMode,
                AutomaticPlanarBitsetCaptureCount =
                    diagnostics.AutomaticPlanarBitsetCaptureCount,
                AutomaticPlanarSortedListFallbackCount =
                    diagnostics.AutomaticPlanarSortedListFallbackCount,
                AutomaticPlanarMetadataSlots =
                    diagnostics.AutomaticPlanarMetadataSlots.ToArray(),
                AutomaticPlanarMetadataPayloadWordCount =
                    diagnostics.AutomaticPlanarMetadataPayloadWordCount,
                AutomaticPlanarMetadataWordsUsed =
                    diagnostics.AutomaticPlanarMetadataWordsUsed,
                AutomaticPlanarMetadataBankHighWaterMark =
                    diagnostics.AutomaticPlanarMetadataBankHighWaterMark,
                AutomaticPlanarMetadataCapacityRejectionCount =
                    diagnostics.AutomaticPlanarMetadataCapacityRejectionCount,
                GpuAutomaticPlanarCaptureMicroseconds =
                    diagnostics.GpuAutomaticPlanarCaptureMicroseconds,
                GpuHybridReflectionSsrMicroseconds =
                    diagnostics.GpuHybridReflectionSsrMicroseconds,
                GpuHybridReflectionRayQueryMicroseconds =
                    diagnostics.GpuHybridReflectionRayQueryMicroseconds,
                GpuHybridReflectionDdgiBaseMicroseconds =
                    diagnostics.GpuHybridReflectionDdgiBaseMicroseconds,
                GpuHybridReflectionResolveMicroseconds =
                    diagnostics.GpuHybridReflectionResolveMicroseconds,
                GpuHybridReflectionTemporalMicroseconds =
                    diagnostics.GpuHybridReflectionTemporalMicroseconds,
                GpuHybridReflectionSpatialMicroseconds =
                    diagnostics.GpuHybridReflectionSpatialMicroseconds,
                GpuHybridReflectionCompositeMicroseconds =
                    diagnostics.GpuHybridReflectionCompositeMicroseconds,
                TransparencyMode = diagnostics.TransparencyMode,
                TransparentSceneReflectionSsrSampleBudget =
                    diagnostics.TransparentSceneReflectionSsrSampleBudget,
                TransparentReflectionRayRequestCount =
                    diagnostics.TransparentReflectionRayRequestCount,
                TransparentReflectionExactSsrEligibleCount =
                    diagnostics.TransparentReflectionExactSsrEligibleCount,
                TransparentReflectionExactSsrAdmittedCount =
                    diagnostics.TransparentReflectionExactSsrAdmittedCount,
                TransparentReflectionExactSsrReservedSampleCount =
                    diagnostics.TransparentReflectionExactSsrReservedSampleCount,
                TransparentReflectionExactSsrActualSampleCount =
                    diagnostics.TransparentReflectionExactSsrActualSampleCount,
                TransparentReflectionExactSsrHitCount =
                    diagnostics.TransparentReflectionExactSsrHitCount,
                TransparentReflectionExactSsrBudgetRejectedCount =
                    diagnostics.TransparentReflectionExactSsrBudgetRejectedCount,
                TransparentReflectionExactRayAdmittedCount =
                    diagnostics.TransparentReflectionExactRayAdmittedCount,
                TransparentReflectionExactRayBudgetRejectedCount =
                    diagnostics.TransparentReflectionExactRayBudgetRejectedCount
            };
            _frames.Add(frame);
        }

        if (absoluteFrameIndex <
            SampleBistroQualityCaptureContract.TotalCaptureFrameCount - 1)
        {
            return;
        }

        if (diagnostics.ScreenshotPendingCount == 0 &&
            AllRequestedArtifactsExist())
        {
            Complete();
            return;
        }

        _completionWaitFrames++;
        if (_completionWaitFrames <= ScreenshotCompletionTimeoutFrames)
            return;

        Fail(
            $"Screenshot completion exceeded {ScreenshotCompletionTimeoutFrames} frames; " +
            $"pending={diagnostics.ScreenshotPendingCount}.");
    }

    private bool AllRequestedArtifactsExist() =>
        _requestedArtifacts.Values.All(relativePath =>
        {
            string path = Path.Combine(_outputDirectory, relativePath);
            return File.Exists(path) && new FileInfo(path).Length > 0;
        });

    private void Complete()
    {
        SampleBistroQualityGateResult gate = EvaluateGate();
        _completed = true;
        string failure = gate.Passed
            ? string.Empty
            : string.Join(" ", gate.Failures);
        WriteReport(gate.Passed ? "completed" : "failed", failure, gate);
        _controller.Restore();
        if (!gate.Passed)
            Environment.ExitCode = 1;
        _exit();
    }

    private void Fail(string failure)
    {
        _completed = true;
        WriteReport("failed", failure);
        _controller.Restore();
        Environment.ExitCode = 1;
        _exit();
    }

    private SampleBistroQualityGateResult EvaluateGate()
    {
        var failures = new List<string>();
        bool completeFrameSet = _frames.Count ==
            SampleBistroQualityCaptureContract.LoopFrameCount;
        if (!completeFrameSet)
        {
            failures.Add(
                $"Expected {SampleBistroQualityCaptureContract.LoopFrameCount} " +
                $"telemetry frames, observed {_frames.Count}.");
        }

        bool projectionStable = _frames
            .Select(static frame => frame.Camera.ProjectionHash)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .Count() <= 1;
        if (!projectionStable)
            failures.Add("The camera projection changed during the capture loop.");

        bool exposureStable = _frames.Count <= 1 ||
            _frames.Max(static frame => frame.Exposure) -
                _frames.Min(static frame => frame.Exposure) <= 1.0e-6f;
        if (!exposureStable)
            failures.Add("Exposure changed during the fixed-exposure capture loop.");

        bool cameraMovedEveryFrame = true;
        if (_controller.Contract.UsesContinuousCameraMotion)
        {
            for (int index = 1; index < _frames.Count; index++)
            {
                PerformanceCaptureCameraMetadata previous =
                    _frames[index - 1].Camera;
                PerformanceCaptureCameraMetadata current = _frames[index].Camera;
                float dx = current.PositionX - previous.PositionX;
                float dy = current.PositionY - previous.PositionY;
                float dz = current.PositionZ - previous.PositionZ;
                if (dx * dx + dy * dy + dz * dz <= 1.0e-8f)
                {
                    cameraMovedEveryFrame = false;
                    break;
                }
            }
            if (!cameraMovedEveryFrame)
                failures.Add("The camera did not move on every rendered test frame.");
        }

        bool cameraCutFree = _frames
            .Select(static frame => frame.Camera.CameraCutSerial)
            .Distinct()
            .Take(2)
            .Count() <= 1;
        if (!cameraCutFree)
            failures.Add("A camera cut was reported during the continuous path.");

        SampleBistroQualityFrameTelemetry? firstMeasuredFrame =
            _frames.Count > 0 ? _frames[0] : null;
        // A moving toroidal field can defer the optional multi-frame frozen
        // solve/audit while remaining completely usable. Require a current live
        // GPU publication before the measured lighting event. Newly exposed
        // scroll slabs are deliberately invalidated and repaired behind that
        // publication, just as Flax preserves the overlapping toroidal field and
        // clears only its entering planes. Only stationary captures require no
        // stale source cells and an empty background solve; demanding either from
        // continuous scrolling makes the gate depend on the exact loop boundary.
        bool requireSettledStationaryTail =
            !_controller.Contract.UsesContinuousCameraMotion;
        SampleBistroQualityFrameTelemetry? warmupBoundaryFrame = _frames
            .TakeWhile(static frame =>
                frame.LoopFrameIndex <
                    SampleBistroQualityCaptureContract.LightingEventStartFrame)
            .FirstOrDefault(frame =>
                IsLivePropagationBoundary(frame) &&
                (!requireSettledStationaryTail ||
                 (frame.SimpleDdgiTransportGlobalConvergencePending == 0 &&
                  frame.SimpleDdgiTransportPendingSolverProbeCount == 0 &&
                  frame.SimpleDdgiTransportSourceStaleProbeCount == 0)));
        bool warmupConverged = warmupBoundaryFrame is not null;
        if (!warmupConverged)
        {
            failures.Add(
                "DDGI did not expose a current live publication " +
                "before the measured lighting event.");
        }

        bool schedulerFeedbackStable = firstMeasuredFrame is not null;
        for (int index = 0; schedulerFeedbackStable && index < _frames.Count; index++)
        {
            SampleBistroQualityFrameTelemetry frame = _frames[index];
            schedulerFeedbackStable = frame.SimpleDdgiSchedulerReady != 0 &&
                frame.SimpleDdgiSchedulerFallbackCount == 0UL &&
                (frame.SimpleDdgiSchedulerFeedbackValid != 0 ||
                 IsExpectedSchedulerFeedbackTransition(_frames, index));
        }
        if (!schedulerFeedbackStable)
        {
            failures.Add(
                "The GPU-resident DDGI scheduler lost valid feedback or " +
                "entered fallback during the measured loop.");
        }

        bool tailCertified = firstMeasuredFrame is not null &&
            (!firstMeasuredFrame.SimpleDdgiTransportTailCertificationEnabled ||
             firstMeasuredFrame.SimpleDdgiTransportTailPhase ==
                 SimpleDdgiTransportPhase.Certified);
        if (!tailCertified && !_controller.Contract.UsesContinuousCameraMotion)
        {
            failures.Add(
                $"The DDGI transport tail was not certified after warmup " +
                $"(phase={firstMeasuredFrame?.SimpleDdgiTransportTailPhase}, " +
                 $"reason={firstMeasuredFrame?.SimpleDdgiTransportTailReason}).");
        }
        if (_controller.Contract.UsesContinuousCameraMotion &&
            _frames.Any(static frame =>
                frame.SimpleDdgiTransportTailPhase ==
                    SimpleDdgiTransportPhase.FailClosedRecovery))
        {
            failures.Add(
                "The moving DDGI field entered destructive tail recovery after " +
                "a usable live propagation boundary was available.");
        }

        int recenteredFrames = _frames.Count(static frame =>
            frame.SimpleDdgiRecentered > 0);
        int preservedFrames = _frames.Count(static frame =>
            frame.SimpleDdgiAtlasPreservedOnRecenter > 0);
        int clearedFrames = _frames.Count(static frame =>
            frame.SimpleDdgiAtlasCleared > 0);
        int receiverMapFullClearFrames = _frames.Count(static frame =>
            frame.SimpleDdgiReceiverFullClear > 0);
        if (_controller.Contract.UsesContinuousCameraMotion &&
            recenteredFrames == 0)
        {
            failures.Add(
                "The motion path did not trigger a DDGI volume recenter.");
        }
        if (clearedFrames != 0)
            failures.Add("DDGI cleared an atlas while the camera was moving.");
        if (receiverMapFullClearFrames != 0)
        {
            failures.Add(
                $"DDGI cleared the complete compact receiver map on " +
                $"{receiverMapFullClearFrames} moving-camera frames.");
        }

        bool transportTopologyStable = _frames
            .Select(static frame =>
                frame.SimpleDdgiTransportTopologyGeneration)
            .Distinct()
            .Take(2)
            .Count() <= 1;
        bool logicalVolumeTableScrolled = _frames
            .Select(static frame => frame.SimpleDdgiVolumeResourceGeneration)
            .Distinct()
            .Take(2)
            .Count() > 1;
        ulong compatibleScrolls = CounterDelta(
            _frames,
            static frame => frame.SimpleDdgiCompatibleToroidalScrollCount);
        ulong incompatibleChanges = CounterDelta(
            _frames,
            static frame => frame.SimpleDdgiIncompatibleTopologyChangeCount);
        ulong convergenceRestarts = CounterDelta(
            _frames,
            static frame => frame.SimpleDdgiGlobalConvergenceRestartCount);
        ulong wholeReadbackDrops = CounterDelta(
            _frames,
            static frame => frame.SimpleDdgiWholeReadbackDropCount);
        int schedulerFullRebuilds = _frames.Sum(static frame =>
            Math.Max(0, frame.SimpleDdgiSchedulerFullRebuildCount));
        if (_controller.Contract.UsesContinuousCameraMotion)
        {
            if (!logicalVolumeTableScrolled || compatibleScrolls == 0UL)
            {
                failures.Add(
                    "The moving-camera loop did not exercise a compatible " +
                    "toroidal DDGI table scroll.");
            }
            if (!transportTopologyStable)
            {
                failures.Add(
                    "A compatible camera scroll changed the DDGI transport " +
                    "topology generation.");
            }
            if (incompatibleChanges != 0UL)
            {
                failures.Add(
                    $"Camera motion caused {incompatibleChanges} incompatible " +
                    "DDGI topology changes.");
            }
            if (wholeReadbackDrops != 0UL)
            {
                failures.Add(
                    $"Camera motion dropped {wholeReadbackDrops} whole DDGI " +
                    "readback records.");
            }
        }
        if (_controller.Contract.Variant ==
            SampleBistroQualityCaptureVariant.SteadyMotion)
        {
            if (convergenceRestarts != 0UL)
            {
                failures.Add(
                    $"Steady camera motion restarted global DDGI convergence " +
                    $"{convergenceRestarts} times.");
            }
            if (schedulerFullRebuilds != 0)
            {
                failures.Add(
                    $"Steady camera motion rebuilt the full DDGI scheduler " +
                    $"{schedulerFullRebuilds} times.");
            }
        }

        int generationResponseFrames = -1;
        int firstUpdateP95 = _frames.Count == 0
            ? 0
            : _frames[^1].DirtyFirstUpdateLatencyP95Frames;
        int convergenceP95 = _frames.Count == 0
            ? 0
            : _frames[^1].DirtyConvergenceLatencyP95Frames;
        int visibleRelightProbeTarget = 0;
        int visibleRelightProbeUpdates = 0;
        bool relightVariant = _controller.Contract.Variant is
            SampleBistroQualityCaptureVariant.SunScaleStep or
            SampleBistroQualityCaptureVariant.SunDirectionStep;
        if (relightVariant && _frames.Count >
                SampleBistroQualityCaptureContract.LightingEventStartFrame)
        {
            SampleBistroQualityFrameTelemetry? baseline = _frames
                .LastOrDefault(static frame =>
                    frame.LoopFrameIndex <
                    SampleBistroQualityCaptureContract.LightingEventStartFrame);
            SampleBistroQualityFrameTelemetry? generationFrame = baseline is null
                ? null
                : _frames.FirstOrDefault(frame =>
                    frame.LoopFrameIndex >=
                        SampleBistroQualityCaptureContract.LightingEventStartFrame &&
                    frame.SimpleDdgiSourceLightingGeneration !=
                        baseline.SimpleDdgiSourceLightingGeneration);
            if (generationFrame is not null)
            {
                generationResponseFrames = generationFrame.LoopFrameIndex -
                    SampleBistroQualityCaptureContract.LightingEventStartFrame;
            }
            if (generationResponseFrames is < 0 or > 1)
            {
                failures.Add(
                    $"DDGI source generation response was " +
                    $"{generationResponseFrames} frames; the limit is 1.");
            }

            SampleBistroQualityFrameTelemetry latest = _frames[^1];
            if (latest.DirtyFirstUpdateLatencySampleCount <= 0)
            {
                SampleBistroQualityFrameTelemetry? firstWork = _frames
                    .FirstOrDefault(static frame =>
                        frame.LoopFrameIndex >=
                            SampleBistroQualityCaptureContract
                                .LightingEventStartFrame &&
                        frame.SimpleDdgiLightingDirtyFrames > 0 &&
                        frame.SimpleDdgiTransportSourceRefreshProbeCount > 0);
                firstUpdateP95 = firstWork is null
                    ? -1
                    : firstWork.LoopFrameIndex -
                        SampleBistroQualityCaptureContract
                            .LightingEventStartFrame;
            }
            if (firstUpdateP95 is < 0 or >
                    SampleBistroQualityCaptureContract
                        .SchedulerFeedbackTransitionGraceFrames)
            {
                failures.Add(
                    $"DDGI first visible update response was {firstUpdateP95} frames; " +
                    $"the fence-complete feedback limit is " +
                    $"{SampleBistroQualityCaptureContract.SchedulerFeedbackTransitionGraceFrames}.");
            }

            if (latest.DirtyConvergenceLatencySampleCount <= 0)
            {
                SampleBistroQualityFrameTelemetry? visibleConvergence =
                    _controller.Contract.Variant ==
                        SampleBistroQualityCaptureVariant.SunScaleStep
                        ? _frames.FirstOrDefault(static frame =>
                            frame.LoopFrameIndex >=
                                SampleBistroQualityCaptureContract
                                    .LightingEventStartFrame &&
                            frame.SimpleDdgiUrgentRelightCommittedCount > 0)
                        : null;
                // GPU-resident mode intentionally avoids CPU latency
                // readbacks. Measure a receiver-derived visible cohort rather
                // than mistaking an unrelated maintenance update for
                // convergence. Source-repair scheduling visits each stale
                // generation once, so the cumulative count is a committed
                // unique-probe witness during this transition.
                ulong observedVisibleProbeTarget = _frames
                    .Where(static frame =>
                        frame.LoopFrameIndex <
                            SampleBistroQualityCaptureContract
                                .LightingEventStartFrame)
                    .Select(static frame =>
                        (ulong)frame
                            .SimpleDdgiReceiverContributingProbeCount +
                        frame.SimpleDdgiReceiverFallbackProbeCount)
                    .DefaultIfEmpty(0UL)
                    .Max();
                visibleRelightProbeTarget = checked((int)Math.Min(
                    int.MaxValue,
                    observedVisibleProbeTarget > 0
                        ? observedVisibleProbeTarget
                        : (ulong)SimpleDdgiUrgentRelightPolicy
                            .MaximumProbeBudget));
                foreach (SampleBistroQualityFrameTelemetry frame in _frames)
                {
                    if (frame.LoopFrameIndex <
                            SampleBistroQualityCaptureContract
                                .LightingEventStartFrame ||
                        frame.SimpleDdgiLightingDirtyFrames <= 0)
                    {
                        continue;
                    }

                    visibleRelightProbeUpdates = checked(
                        visibleRelightProbeUpdates +
                        Math.Max(
                            0,
                            frame.SimpleDdgiTransportSourceRefreshProbeCount));
                    if (visibleRelightProbeUpdates < visibleRelightProbeTarget)
                        continue;

                    visibleConvergence = frame;
                    break;
                }
                convergenceP95 = visibleConvergence is null
                    ? -1
                    : visibleConvergence.LoopFrameIndex -
                        SampleBistroQualityCaptureContract
                            .LightingEventStartFrame;
            }
            if (convergenceP95 is < 0 or > 8)
            {
                failures.Add(
                    $"DDGI visible convergence response was {convergenceP95} frames; " +
                    "the limit is 8.");
            }
        }

        int reflectionProbeCount = _frames.Count == 0
            ? 0
            : _frames.Max(static frame => frame.ReflectionProbeCount);
        if (reflectionProbeCount != 0)
        {
            failures.Add(
                $"Bistro must remain probe-free; observed " +
                $"{reflectionProbeCount} manual reflection probe(s).");
        }
        if (_frames.Any(static frame =>
                frame.ReflectionProbeCapturesQueued != 0 ||
                frame.ReflectionProbeCapturesCompleted != 0 ||
                frame.ReflectionProbePublishedCount != 0 ||
                frame.GpuReflectionProbeCaptureMicroseconds != 0 ||
                frame.GpuReflectionProbePrefilterMicroseconds != 0))
        {
            failures.Add(
                "Bistro recorded reflection-probe capture or publication work.");
        }

        SampleBistroQualityFrameTelemetry[] validReflectionFrames = _frames
            .Where(static frame =>
                frame.HybridReflectionCountersReadbackValid != 0)
            .ToArray();
        bool hybridReflectionTelemetryValid =
            validReflectionFrames.Length > 0;
        ulong ddgiReflectionFallbacks = validReflectionFrames.Aggregate(
            0UL,
            static (total, frame) => total +
                frame.HybridReflectionDdgiFallbackCount);
        ulong probeReflectionFallbacks = validReflectionFrames.Aggregate(
            0UL,
            static (total, frame) => total +
                frame.HybridReflectionProbeFallbackCount);
        ulong environmentReflectionFallbacks = validReflectionFrames.Aggregate(
            0UL,
            static (total, frame) => total +
                frame.HybridReflectionEnvironmentFallbackCount);
        if (!hybridReflectionTelemetryValid)
        {
            failures.Add(
                "Bistro did not publish hybrid-reflection counter telemetry.");
        }
        if (ddgiReflectionFallbacks == 0UL)
        {
            failures.Add(
                "Bistro did not resolve any reflection receivers from DDGI.");
        }
        if (probeReflectionFallbacks != 0UL)
        {
            failures.Add(
                $"Bistro resolved {probeReflectionFallbacks} reflection " +
                "receivers from manual probes; DDGI must be the default.");
        }
        if (!_frames.Any(static frame =>
                frame.GpuHybridReflectionDdgiBaseMicroseconds > 0))
        {
            failures.Add(
                "Bistro did not record GPU work for the DDGI reflection base.");
        }

        return new SampleBistroQualityGateResult(
            failures.Count == 0,
            projectionStable,
            exposureStable,
            cameraMovedEveryFrame,
            cameraCutFree,
            warmupConverged,
            schedulerFeedbackStable,
            tailCertified,
            transportTopologyStable,
            logicalVolumeTableScrolled,
            recenteredFrames,
            preservedFrames,
            clearedFrames,
            receiverMapFullClearFrames,
            compatibleScrolls,
            incompatibleChanges,
            convergenceRestarts,
            wholeReadbackDrops,
            schedulerFullRebuilds,
            generationResponseFrames,
            firstUpdateP95,
            convergenceP95,
            visibleRelightProbeTarget,
            visibleRelightProbeUpdates,
            reflectionProbeCount,
            failures)
        {
            HybridReflectionTelemetryValid = hybridReflectionTelemetryValid,
            HybridReflectionDdgiFallbackCount = ddgiReflectionFallbacks,
            HybridReflectionProbeFallbackCount = probeReflectionFallbacks,
            HybridReflectionEnvironmentFallbackCount =
                environmentReflectionFallbacks
        };
    }

    private static ulong CounterDelta(
        IReadOnlyList<SampleBistroQualityFrameTelemetry> frames,
        Func<SampleBistroQualityFrameTelemetry, ulong> selector)
    {
        if (frames.Count <= 1)
            return 0UL;
        ulong first = selector(frames[0]);
        ulong last = selector(frames[^1]);
        return last >= first ? last - first : ulong.MaxValue;
    }

    private static bool IsLivePropagationBoundary(
        SampleBistroQualityFrameTelemetry frame) =>
        HasCurrentLivePropagationPublication(
            frame.SimpleDdgiSourceLightingGeneration,
            frame.SimpleDdgiLivePropagationSourceGeneration,
            frame.SimpleDdgiTransportGeneration,
            frame.SimpleDdgiPublishedPropagationGeneration,
            frame.SimpleDdgiTransportSourceReadyProbeCount);

    internal static bool HasCurrentLivePropagationPublication(
        uint sourceLightingGeneration,
        uint livePropagationSourceGeneration,
        uint transportGeneration,
        uint publishedPropagationGeneration,
        int sourceReadyProbeCount) =>
        sourceLightingGeneration != 0u &&
        livePropagationSourceGeneration == sourceLightingGeneration &&
        transportGeneration != 0u &&
        publishedPropagationGeneration == transportGeneration &&
        sourceReadyProbeCount > 0;

    private static bool IsExpectedSchedulerFeedbackTransition(
        IReadOnlyList<SampleBistroQualityFrameTelemetry> frames,
        int index)
    {
        if ((uint)index >= (uint)frames.Count ||
            frames[index].SimpleDdgiSchedulerFeedbackValid != 0)
        {
            return false;
        }

        uint generation = frames[index].SimpleDdgiSourceLightingGeneration;
        int first = Math.Max(
            0,
            index - SampleBistroQualityCaptureContract
                .SchedulerFeedbackTransitionGraceFrames);
        for (int prior = index - 1; prior >= first; prior--)
        {
            if (frames[prior].SimpleDdgiSourceLightingGeneration != generation)
                return true;
        }
        return false;
    }

    private void WriteReport(
        string status,
        string failure,
        SampleBistroQualityGateResult? gate = null)
    {
        SampleBistroQualityArtifact[] artifacts = _requestedArtifacts
            .Select(pair => CreateArtifact(pair.Key, pair.Value))
            .ToArray();
        var report = new SampleBistroQualityRunReport(
            "njulf-bistro-quality-capture",
            SampleBistroQualityCaptureContract.Schema,
            DateTimeOffset.UtcNow,
            status,
            _controller.Contract.Variant,
            _controller.Contract.Fingerprint,
            _controller.Contract.CameraPathFingerprint,
            _controller.Contract.LightingScriptFingerprint,
            SampleBistroQualityCaptureContract.Width,
            SampleBistroQualityCaptureContract.Height,
            SampleBistroQualityCaptureContract.FramesPerSecond,
            _frames.ToArray(),
            artifacts,
            gate,
            failure);
        byte[] payload = SerializeReport(report);
        SampleEvidenceFileIo.WriteAtomic(
            Path.Combine(_outputDirectory, "bistro-quality-run.json"),
            payload,
            SampleEvidenceFileIo.MaximumJsonBytes,
            "Bistro quality capture report");
    }

    internal static byte[] SerializeReport(
        SampleBistroQualityRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
    }

    private SampleBistroQualityArtifact CreateArtifact(
        string name,
        string relativePath)
    {
        string path = Path.Combine(_outputDirectory, relativePath);
        if (!File.Exists(path))
            return new SampleBistroQualityArtifact(
                name,
                relativePath,
                0,
                "pending");

        var info = new FileInfo(path);
        using FileStream stream = File.OpenRead(path);
        byte[] hash = SHA256.HashData(stream);
        return new SampleBistroQualityArtifact(
            name,
            relativePath,
            info.Length,
            Convert.ToHexString(hash).ToLowerInvariant());
    }
}
