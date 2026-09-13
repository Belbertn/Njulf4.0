using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Graphics;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Data
{
    public enum DdgiQualityTier : uint
    {
        DdgiLow = 0,
        DdgiMedium = 1,
        DdgiHigh = 2,
        DdgiUltra = 3
    }

    /// <summary>
    /// Determines how the vertical centre of the simple-DDGI camera rings is
    /// resolved.  Receiver-anchored placement is useful for tall architecture:
    /// it prevents a camera moving between floors from silently changing the
    /// resolution assigned to a fixed world-space receiver.
    /// </summary>
    public enum SimpleDdgiVerticalRingPolicy : uint
    {
        CameraRelative = 0,
        CameraRelativeWithHysteresis = 1,
        ReceiverAnchored = 2
    }

    /// <summary>
    /// Semantic intent for authored simple-DDGI coverage.  It is serialized with
    /// the volume so layout admission, scheduling, diagnostics, and tooling can
    /// preserve receiver importance instead of treating all authored boxes as
    /// interchangeable navigation volumes.
    /// </summary>
    public enum SimpleDdgiVolumePurpose : uint
    {
        ReceiverHero = 0,
        NavigableInterior = 1,
        DynamicInfluence = 2,
        TransitionSupport = 3
    }

    /// <summary>
    /// Controls whether a profile that cannot fit the resolved tier fails before
    /// allocation (development/validation) or uses the compiler's explicit,
    /// recorded degraded layout (shipping).
    /// </summary>
    public enum SimpleDdgiLayoutAdmissionMode : uint
    {
        Degrade = 0,
        Reject = 1
    }

    public enum GlobalIlluminationDebugView : uint
    {
        None = 0,
        FinalIndirect = 1,
        DdgiIrradiance = 7,
        DdgiVisibility = 8,
        DdgiProbeIndex = 9,
        DdgiProbeState = 10,
        DdgiProbeRelocation = 11,
        DdgiLeakClamp = 12,
        DdgiCoverage = 14,
        DdgiCascadeSelection = 15,
        DdgiCascadeBlendWeight = 16,
        DdgiUpdateReasons = 17,
        DdgiRayBudget = 18,
        DdgiGatherLocalVolume = 19,
        DdgiGatherClipmap = 20,
        DdgiGatherClipmapBlendWeight = 21,
        DdgiGatherFallback = 22,
        DdgiRawDiffuse = 23,
        DdgiSuppressionMask = 24,
        DdgiEffectiveWeight = 25,
        DdgiEnvironmentFallbackWeight = 26,
        DdgiClassificationInvalidScore = 28,
        DdgiVisibilityMoments = 29,
        DdgiSpatialCoverage = 30,
        DdgiSupportCoverage = 31,
        DdgiDataConfidence = 32,
        DdgiVisibilityConfidence = 33,
        DdgiConfidenceChain = 34,
        DdgiProbeLogicalPosition = 35,
        DdgiProbeRelocatedPosition = 36,
        DdgiProbeRelocationDirection = 37,
        DdgiGatherBlendWeight = 38,
        DdgiSampledIrradiance = 39,
        DdgiFinalDiffuse = 40,
        DdgiConfidenceBypass = 41,
        FarFieldOccupancySlice = 42,
        FarFieldTraceResult = 43,
        FarFieldSkyVisibility = 44,
        FarFieldSunShadow = 45,
        /// <summary>
        /// Exact mutually-exclusive DDGI hit-source classification aggregated
        /// over the last fence-complete diagnostic frame. Red is detailed
        /// textured transport, green is compact profile transport, yellow is
        /// the explicit correctness fallback, and blue is far-field transport.
        /// Counts are deterministic sparse weighted estimates; unlike view 46,
        /// the source labels describe paths that actually executed.
        /// </summary>
        MaterialTransportHitProvenance = 49,
        /// <summary>
        /// Geometric authority of the structured DDGI estimator. This is kept
        /// separate from probe-data availability and transport visibility.
        /// </summary>
        DdgiDirectionalSupport = 50,
        /// <summary>
        /// Direct, emissive, and sky source-cache irradiance sampled with the
        /// receiver's normal and cascade weights, before recursive bounce.
        /// </summary>
        DdgiSourceCacheRadiance = 51,
        DdgiProbeResidency = 52,
        DdgiResidencyFallback = 53,
        DdgiPageAge = 54,
        DdgiPhysicalPage = 55,
        C5SourceRadiance = 56,
        C5RawCandidate = 57,
        C5NearEstimate = 58,
        C5LowEstimate = 59,
        C5SignedResidual = 60,
        C5FinalContribution = 61,
        C5Confidence = 62,
        C5HistoryLength = 63,
        C5HistoryRejectionReason = 64,
        C5TraceDistanceHitValidity = 65,
        C5TileActivity = 66,
        C5B3Footprint = 67,
        /// <summary>
        /// Surface-aware receiver-cache admission. Green is accepted; magenta
        /// is invalid/non-finite; red is depth/position; orange is plane;
        /// blue is normal; yellow is insufficient support. Every non-green
        /// fragment executes the exact fallback.
        /// </summary>
        DdgiReceiverCacheRejection = 68
    }

    /// <summary>
    /// One optional, world-space local quality override layered on top of the
    /// camera-relative rings. Scenes do not need authored volumes for baseline
    /// DDGI coverage; add one explicitly only where denser probes are wanted.
    /// </summary>
    public readonly struct SimpleDdgiAuthoredVolume
    {
        /// <param name="latticePhase">
        /// Spacing-relative lattice phase. Each component is wrapped to [0, 1)
        /// when the authored grid is built, allowing an artist to move probes off
        /// a repeated wall/column alignment without moving the volume bounds.
        /// </param>
        public SimpleDdgiAuthoredVolume(
            Vector3 min,
            Vector3 max,
            float spacing,
            Vector3 latticePhase = default,
            SimpleDdgiVolumePurpose purpose = SimpleDdgiVolumePurpose.ReceiverHero,
            int priority = 0)
        {
            Min = min;
            Max = max;
            Spacing = spacing;
            LatticePhase = latticePhase;
            Purpose = purpose;
            Priority = priority;
        }

        public Vector3 Min { get; init; }
        public Vector3 Max { get; init; }
        public float Spacing { get; init; }
        public Vector3 LatticePhase { get; init; }
        public SimpleDdgiVolumePurpose Purpose { get; init; }
        /// <summary>
        /// Higher values win among overlapping authored volumes.  Priority is
        /// deliberately independent of spacing so a declared receiver-critical
        /// region cannot lose ownership merely because another box is finer.
        /// </summary>
        public int Priority { get; init; }
    }

    public sealed class GlobalIlluminationSettings
    {
        public const int MaxSimpleDdgiMaterialTextureCascade = 4;
        public const ulong DefaultDdgiAtlasMemoryBudgetBytes = 288UL * 1024UL * 1024UL;
        public const int MinSimpleDdgiRaysPerProbe = 16;
        public const int DefaultDdgiProbeUpdatePrimaryRayBudget = 1_024 * MaxSimpleDdgiRaysPerProbe;
        public const int MaxDdgiProbeUpdatePrimaryRayBudget = 16_777_216;
        public const int MaxSimpleDdgiProbeCountX = 64;
        public const int MaxSimpleDdgiProbeCountY = 32;
        public const int MaxSimpleDdgiProbeCountZ = 64;
        public const int MaxSimpleDdgiRaysPerProbe = 256;
        public const int MaxSimpleDdgiVolumeCount = 16;
        public const int MaxSimpleDdgiTotalProbeCount = 32_768;
        // Alias indices occupy 16 bits in the shader ABI. Sponza contains 10,306
        // eligible emissive triangles, so 16K is the smallest power-of-two tier
        // that preserves every production source without approaching the ABI limit.
        public const int MaxDdgiEmissiveTriangleBudget = 16_384;
        public const int MaxFarFieldClipmapResolution = 256;
        public const int MaxSimpleDdgiLocalLightSamplesPerHit = 64;
        public const int MaxSimpleDdgiExactLocalLightThreshold = 1_024;
        public const ulong MaxDdgiDynamicAccelerationStructureBudgetBytes = 8UL * 1024UL * 1024UL * 1024UL;
        public const ulong MaxSimpleDdgiDirectionalRadianceBudgetBytes = 2UL * 1024UL * 1024UL * 1024UL;
        // New settings request the stable advanced-GI production paths.
        // C3 remains an optional, fail-closed sidecar to canonical DDGI: an
        // unavailable or invalid publication falls back to the uniform
        // proposal without stalling probe updates. Hardware capability,
        // memory, ABI, allocation and resource-completeness gates remain
        // authoritative; manifests and promotion evidence apply only to
        // AutoQualified. Persisted Off remains the explicit opt-out.
        public const SimpleDdgiReceiverFeedbackMode
            DefaultSimpleDdgiReceiverFeedbackMode =
                SimpleDdgiReceiverFeedbackMode.ExactCompacted;
        public const DdgiOpacityMicromapMode DefaultDdgiOpacityMicromapMode =
            DdgiOpacityMicromapMode.ExtFourStateExperiment;
        public const SimpleDdgiDirectionalGuidingMode
            DefaultSimpleDdgiDirectionalGuidingMode =
                SimpleDdgiDirectionalGuidingMode
                    .PerProbeHistogramExperiment;
        public const GiCausticMode DefaultGiCausticMode =
            GiCausticMode.WorldCacheExperiment;
        public const SimpleDdgiNearFieldResidualMode
            DefaultSimpleDdgiNearFieldResidualMode =
                SimpleDdgiNearFieldResidualMode.HiZAdaptive;
        public const SimpleDdgiNearFieldResidualQualityPreset
            DefaultSimpleDdgiNearFieldResidualQualityPreset =
                SimpleDdgiNearFieldResidualQualityPreset.Balanced;
        // The adaptive receiver cache remains an explicit experiment. Native
        // 1080p qualification found that its producer/feedback work cost more
        // than the exact gather and changed stable pixels outside the A/A
        // envelope, so production presets default to the canonical exact path.
        public const SimpleDdgiReceiverCacheMode
            DefaultSimpleDdgiReceiverCacheMode =
                SimpleDdgiReceiverCacheMode.Exact;

        private float _indirectIntensity = 1.0f;
        private float _environmentFallbackIntensity = 1.0f;
        private float _resolutionScale = 0.5f;
        private float _maxBounceDistance = 6.0f;
        private float _historyResponsiveness = 0.18f;
        private float _normalRejectionThreshold = 0.85f;
        private float _depthRejectionThreshold = 0.08f;
        private float _leakClampStrength = 0.75f;
        private int _ddgiProbeUpdatePrimaryRayBudget = DefaultDdgiProbeUpdatePrimaryRayBudget;
        private int _ddgiMaxShadedLights = 8;
        private int _ddgiMaterialTextureMaxCascade = 1;
        private int _ddgiEmissiveTriangleBudget = MaxDdgiEmissiveTriangleBudget;
        private ulong _ddgiAtlasMemoryBudgetBytes = DefaultDdgiAtlasMemoryBudgetBytes;
        private float _ddgiThinWallLeakClampStrength = 0.9f;
        private float _ddgiSelfShadowBiasScale = 1.0f;
        private float _simpleDdgiProbeSpacing = 1.25f;
        private int _simpleDdgiRingCount = 3;
        private float _simpleDdgiRingBaseSpacing = 1.25f;
        private float _simpleDdgiRingSpacingMultiplier = 3.0f;
        private float _simpleDdgiViewForwardPlacementFraction = 0.6f;
        private SimpleDdgiVerticalRingPolicy _simpleDdgiVerticalRingPolicy = SimpleDdgiVerticalRingPolicy.CameraRelativeWithHysteresis;
        private float _simpleDdgiReceiverVerticalAnchor = 4.5f;
        private float _simpleDdgiVerticalRecenterHysteresisFraction = 0.25f;
        private int _simpleDdgiNearRingGridSizeX = 28;
        private int _simpleDdgiNearRingGridSizeY = 14;
        private int _simpleDdgiNearRingGridSizeZ = 28;
        private int _simpleDdgiMidRingGridSizeX = 18;
        private int _simpleDdgiMidRingGridSizeY = 10;
        private int _simpleDdgiMidRingGridSizeZ = 18;
        private int _simpleDdgiFarRingGridSizeX = 12;
        private int _simpleDdgiFarRingGridSizeY = 8;
        private int _simpleDdgiFarRingGridSizeZ = 12;
        private bool _simpleDdgiRefinementBricksEnabled = true;
        private int _simpleDdgiRefinementMaximumBricks = 2;
        private int _simpleDdgiRefinementGridSizeX = 6;
        private int _simpleDdgiRefinementGridSizeY = 4;
        private int _simpleDdgiRefinementGridSizeZ = 6;
        private float _simpleDdgiRefinementSpacingScale = 0.5f;
        private int _simpleDdgiRefinementRetentionFrames = 90;
        private float _simpleDdgiRefinementMinimumEmissiveLuminanceNits = 200f;
        private float _simpleDdgiRefinementMaximumEmitterAreaSquareMeters = 4f;
        private bool _simpleDdgiNearVisibilitySidecarEnabled = true;
        private ulong _simpleDdgiNearVisibilitySidecarMemoryBudgetBytes =
            64UL * 1024UL * 1024UL;
        private int _simpleDdgiRaysPerProbe = 96;
        private int _simpleDdgiMaintenanceRaysPerProbe = 24;
        private float _simpleDdgiHysteresis = 0.97f;
        private float _simpleDdgiHysteresisChangeThreshold = 0.50f;
        private float _simpleDdgiHysteresisStepThreshold = 0.80f;
        private int _simpleDdgiLightingDirtyFrameCount = 30;
        private int _simpleDdgiStableMaintenanceUpdateCount = 3;
        private float _simpleDdgiStableMaintenanceEmaThreshold = 0.03f;
        private float _simpleDdgiTransportSolverRelaxation = 0.70f;
        private float _simpleDdgiTransportAlbedoClamp = 0.95f;
        private float _simpleDdgiTransportTailRelativeTolerance = 0.025f;
        private int _simpleDdgiTransportAcceleratedSweepCount = 2;
        // Production tiers request the bounded red-black solve. The certified
        // Jacobi path remains the automatic correctness fallback when runtime
        // validation or resource admission rejects acceleration.
        private bool _simpleDdgiTransportAccelerationEnabled = true;
        private bool _simpleDdgiTransportTailCertificationEnabled = true;
        private int _simpleDdgiTransportMaximumSolverGenerations = 8;
        // The static source-age watchdog is independent of legacy generation
        // counts. Its configured floor leaves room for full-field cached solve,
        // chunked certification, delayed readback, and an identity-locked
        // performance window. Real source/material/transform changes still
        // invalidate immediately and do not wait for this interval.
        private int _simpleDdgiTransportSourceRefreshFrames = 2_048;
        private float _simpleDdgiAutomaticProbeDensityScale = 0.70f;
        private float _simpleDdgiNormalBias = 0.1f;
        private float _simpleDdgiViewBias = 0.3f;
        // Bias remains spacing-aware for self-intersection resistance, but the
        // final displacement must also respect a scene-scale ceiling and a
        // conservative architectural-thickness proxy.  Otherwise a coarse ring
        // can move an interpolation query through a thin wall.
        private float _simpleDdgiMaximumWorldBiasMeters = 0.20f;
        private float _simpleDdgiArchitecturalThicknessMeters = 0.80f;
        private float _simpleDdgiSecondVolumeOwnershipEarlyOutThreshold = 1.0f;
        private int _simpleDdgiSchedulerReentryStableFrameCount = 120;
        private SimpleDdgiProbeResidencyMode _simpleDdgiProbeResidencyMode =
            SimpleDdgiProbeResidencyMode.Dense;
        private int _simpleDdgiSparsePhysicalPageBudget;
        private int _simpleDdgiSparseMinimumPhysicalPageBudget;
        private int _simpleDdgiSparseRetentionFrames = 120;
        private int _simpleDdgiSparseMaximumAdmissionsPerFrame = 64;
        private int _simpleDdgiSparseMaximumReceiverFeedbackRequests = 2_048;
        private int _simpleDdgiSparseInactiveRetryFrames = 300;
        private int _simpleDdgiUrgentRelightProbeBudget = 32;
        private int _simpleDdgiProbeUpdatesPerFrame = 2_048;
        // Independent per-ring controls. The scheduler consumes these explicit
        // values for near/authored, mid, and far camera-relative volumes.
        private int _simpleDdgiNearFullRaysPerProbe = 64;
        private int _simpleDdgiMidFullRaysPerProbe = 48;
        private int _simpleDdgiFarFullRaysPerProbe = 24;
        private int _simpleDdgiNearMaintenanceRaysPerProbe = 32;
        private int _simpleDdgiMidMaintenanceRaysPerProbe = 16;
        private int _simpleDdgiFarMaintenanceRaysPerProbe = 8;
        private int _simpleDdgiNearMinimumUpdateQuota = 512;
        private int _simpleDdgiMidMinimumUpdateQuota = 96;
        private int _simpleDdgiFarMinimumUpdateQuota = 24;
        private int _simpleDdgiNearMaximumUpdateQuota = 1_024;
        private int _simpleDdgiMidMaximumUpdateQuota = 324;
        private int _simpleDdgiFarMaximumUpdateQuota = 128;
        private int _simpleDdgiNearMaterialTextureMaxCascade = 1;
        private int _simpleDdgiMidMaterialTextureMaxCascade = 0;
        private int _simpleDdgiFarMaterialTextureMaxCascade = -1;
        private int _simpleDdgiNearMaxShadedLights = 8;
        private int _simpleDdgiMidMaxShadedLights = 4;
        private int _simpleDdgiFarMaxShadedLights = 2;
        private SimpleDdgiLocalLightSamplingMode _simpleDdgiLocalLightSamplingMode =
            SimpleDdgiLocalLightSamplingMode.Auto;
        private SimpleDdgiDirectionalRadianceMode _simpleDdgiDirectionalRadianceMode =
            SimpleDdgiDirectionalRadianceMode.Off;
        private SimpleDdgiGlossyTransportMode _simpleDdgiGlossyTransportMode =
            SimpleDdgiGlossyTransportMode.Off;
        private DdgiSkinnedGeometryMode _ddgiSkinnedGeometryMode =
            DdgiSkinnedGeometryMode.Excluded;
        private DdgiTransparentGeometryMode _ddgiTransparentGeometryMode =
            DdgiTransparentGeometryMode.MaskOnly;
        private DdgiFoliageGeometryMode _ddgiFoliageGeometryMode =
            DdgiFoliageGeometryMode.Excluded;
        private int _simpleDdgiNearLocalLightSamplesPerHit = 8;
        private int _simpleDdgiMidLocalLightSamplesPerHit = 4;
        private int _simpleDdgiFarLocalLightSamplesPerHit = 2;
        private int _simpleDdgiExactLocalLightThreshold = 8;
        private float _simpleDdgiLightTreeUniformMixtureProbability = 0.02f;
        private int _simpleDdgiLightTreeMaximumRefitAge = 120;
        private ulong _ddgiDynamicBlasMemoryBudgetBytes = 256UL * 1024UL * 1024UL;
        private ulong _ddgiDynamicBlasScratchBudgetBytes = 64UL * 1024UL * 1024UL;
        private int _ddgiDynamicBlasBuildsPerFrame = 16;
        private int _ddgiDynamicBlasPrimitivesPerFrame = 1_000_000;
        private int _ddgiFoliageProxyTriangleBudget = 250_000;
        private int _ddgiFoliageProxyUpdateCadenceFrames = 2;
        private int _ddgiTransparencyCandidateLimit = 32;
        private int _ddgiTransparencyLayerLimit = 8;
        private int _ddgiDecalCandidateLimit = 8;
        private ulong _simpleDdgiDirectionalRadianceMemoryBudgetBytes =
            64UL * 1024UL * 1024UL;
        private float _simpleDdgiRoughSpecularMinimumRoughness = 0.55f;
        private float _simpleDdgiRoughSpecularFullWeightRoughness = 0.70f;
        private int _farFieldClipmapResolution = 128;
        private float _farFieldStartDistance = 12.0f;
        private int _farFieldMaxTraceSteps = 256;
        private int _farFieldPageResolution = 32;
        private int _farFieldCascadeCount = 3;
        private int _farFieldResidentPageBudget = 48;
        private int _farFieldPageUpdatesPerFrame = 1;
        private int _farFieldPageRequestRadius = 1;
        private float _farFieldBaseVoxelSize = 1.0f;
        private float _farFieldCascadeVoxelScale = 3.0f;
        private ulong _farFieldMemoryBudgetBytes = 96UL * 1024UL * 1024UL;
        private ulong _giAccelerationStructureMemoryBudgetBytes = 1024UL * 1024UL * 1024UL;
        private float _giAccelerationStructureStaticResidentDistance = 256.0f;
        private int _giAccelerationStructureMaximumStaticInstances = 8_192;
        private int _giAccelerationStructureEvictionGraceFrames = 120;
        private GlobalIlluminationDebugView _debugView;
        // Versioned advanced-GI intent. These values are persisted; explicit
        // modes are ordinary feature requests and remain bounded only by real
        // device, memory, ABI, allocation, and content requirements.
        private SimpleDdgiReceiverFeedbackMode _simpleDdgiReceiverFeedbackMode =
            DefaultSimpleDdgiReceiverFeedbackMode;
        private DdgiOpacityMicromapMode _ddgiOpacityMicromapMode =
            DefaultDdgiOpacityMicromapMode;
        private SimpleDdgiDirectionalGuidingMode _simpleDdgiDirectionalGuidingMode =
            DefaultSimpleDdgiDirectionalGuidingMode;
        private GiCausticMode _giCausticMode = DefaultGiCausticMode;
        private SimpleDdgiNearFieldResidualMode _simpleDdgiNearFieldResidualMode =
            DefaultSimpleDdgiNearFieldResidualMode;
        private SimpleDdgiReceiverCacheMode _simpleDdgiReceiverCacheMode =
            DefaultSimpleDdgiReceiverCacheMode;
        private SimpleDdgiNearFieldResidualQualityPreset
            _simpleDdgiNearFieldResidualQualityPreset =
                DefaultSimpleDdgiNearFieldResidualQualityPreset;
        private float _simpleDdgiNearFieldResidualMaximumTraceDistanceMeters = 8.0f;
        private int _simpleDdgiNearFieldResidualRaysPerPixel = 2;
        private int _simpleDdgiNearFieldResidualFilterIterationCount = 2;
        private float _simpleDdgiNearFieldResidualIntensity = 1.0f;
        private string _simpleDdgiReceiverFeedbackQualificationId = string.Empty;
        private string _ddgiOpacityMicromapQualificationId = string.Empty;
        private string _simpleDdgiDirectionalGuidingQualificationId = string.Empty;
        private string _giCausticQualificationId = string.Empty;
        private string _simpleDdgiNearFieldResidualQualificationId = string.Empty;

        public bool Enabled { get; set; } = true;
        public GlobalIlluminationMode Mode { get; set; } = GlobalIlluminationMode.Ddgi;
        public GlobalIlluminationDebugView DebugView
        {
            get => _debugView;
            set => _debugView = Enum.IsDefined(value)
                ? value
                : GlobalIlluminationDebugView.None;
        }

        /// <summary>
        /// Emergency runtime kill-switch for dynamic global-illumination paths.
        /// When enabled, the Simple-DDGI and GI ray-query selectors are false. The authored GI
        /// configuration is intentionally retained, so clearing the switch
        /// restores it without affecting environment lighting or reflections.
        /// </summary>
        public bool EmergencyGiFallbackEnabled { get; set; }

        public float IndirectIntensity
        {
            get => _indirectIntensity;
            set => _indirectIntensity = Clamp(value, 0.0f, 8.0f);
        }

        public float EnvironmentFallbackIntensity
        {
            get => _environmentFallbackIntensity;
            set => _environmentFallbackIntensity = Clamp(value, 0.0f, 4.0f);
        }

        public bool UseDdgi { get; set; } = true;
        public bool UseRayQueryBackend { get; set; } = true;
        public DdgiQualityTier DdgiQualityTier { get; set; } = DdgiQualityTier.DdgiHigh;

        /// <summary>
        /// Requests the canonical authored-material transport compiler and V2
        /// shader contract. Runtime consumers use the corresponding
        /// <c>Effective*</c> property, so this switch alone cannot authorize V2.
        /// </summary>
        public bool GiMaterialTransportV2 { get; set; }

        /// <summary>
        /// Requests bounded world-space emissive triangles and deterministic
        /// alias sampling. The effective qualified path and legacy bounds proxy
        /// are mutually exclusive.
        /// </summary>
        public bool GiEmissiveMeshSampling { get; set; }

        /// <summary>
        /// Non-persisted release policy. Qualification evidence must be applied
        /// independently of ordinary render settings so a copied user setting
        /// cannot promote V2 to a shipping default.
        /// </summary>
        public MaterialGiRolloutPolicy MaterialGiRollout { get; } = new();

        /// <summary>
        /// Material-GI V2 features requested by ordinary configuration. This is
        /// not an execution authorization and may include persisted or copied
        /// values that the rollout policy rejects.
        /// </summary>
        public MaterialGiV2Feature ConfiguredMaterialGiV2Features
        {
            get
            {
                MaterialGiV2Feature features = MaterialGiV2Feature.None;
                if (GiMaterialTransportV2)
                    features |= MaterialGiV2Feature.MaterialTransport;
                if (GiEmissiveMeshSampling)
                    features |= MaterialGiV2Feature.EmissiveMeshSampling;
                if (GiFarFieldMaterialV2)
                    features |= MaterialGiV2Feature.FarFieldMaterial;
                return features;
            }
        }

        /// <summary>
        /// Material-GI V2 features authorized for runtime execution by the
        /// non-persisted rollout policy.
        /// </summary>
        public MaterialGiV2Feature ActiveMaterialGiV2Features =>
            MaterialGiRollout.ResolveEffectiveFeatures(
                ConfiguredMaterialGiV2Features);

        public bool EffectiveGiMaterialTransportV2 =>
            IsMaterialGiV2FeatureActive(
                MaterialGiV2Feature.MaterialTransport);

        public bool EffectiveGiEmissiveMeshSampling =>
            IsMaterialGiV2FeatureActive(
                MaterialGiV2Feature.EmissiveMeshSampling);

        public bool EffectiveGiFarFieldMaterialV2 =>
            IsMaterialGiV2FeatureActive(
                MaterialGiV2Feature.FarFieldMaterial);

        public void UseLegacyMaterialGiRollout()
        {
            MaterialGiRollout.UseLegacy();
            ApplyMaterialGiFeatureMask(MaterialGiV2Feature.None);
        }

        /// <summary>
        /// Explicit non-shipping opt-in used by conformance executables and
        /// samples. Release qualification remains unavailable in this mode.
        /// </summary>
        public void EnableMaterialGiV2ForConformance(
            MaterialGiV2Feature features = MaterialGiV2Feature.All)
        {
            MaterialGiRollout.EnableConformance(features);
            ApplyMaterialGiFeatureMask(features);
        }

        /// <summary>
        /// Explicit non-shipping mode for producing the performance matrix
        /// consumed by a first release qualification. Unlike conformance mode,
        /// diagnostics mark qualification as required, but never as approved.
        /// </summary>
        public void EnableMaterialGiV2ForQualificationCandidate()
        {
            MaterialGiRollout.EnableQualificationCandidate();
            ApplyMaterialGiFeatureMask(MaterialGiV2Feature.All);
        }

        public void ApplyMaterialGiV2Qualification(
            MaterialGiRolloutQualificationManifest manifest,
            DateOnly? evaluationDate = null)
        {
            MaterialGiRollout.ApplyQualification(manifest, evaluationDate);
            ApplyMaterialGiFeatureMask(manifest.EnabledFeatures);
        }

        public void ApplyMaterialGiV2Qualification(
            string manifestPath,
            DateOnly? evaluationDate = null) =>
            ApplyMaterialGiV2Qualification(
                MaterialGiRolloutQualificationManifest.Load(manifestPath),
                evaluationDate);

        public MaterialGiRolloutEvaluation EvaluateMaterialGiRollout(
            DateOnly? evaluationDate = null)
        {
            MaterialGiV2Feature configuredFeatures =
                ConfiguredMaterialGiV2Features;
            MaterialGiV2Feature activeFeatures =
                MaterialGiRollout.ResolveEffectiveFeatures(
                    configuredFeatures,
                    evaluationDate);
            return MaterialGiRollout
                .Evaluate(configuredFeatures, evaluationDate) with
            {
                ActiveFeatures = activeFeatures
            };
        }

        private bool IsMaterialGiV2FeatureActive(
            MaterialGiV2Feature feature) =>
            (ActiveMaterialGiV2Features & feature) != 0;

        private void ApplyMaterialGiFeatureMask(MaterialGiV2Feature features)
        {
            GiMaterialTransportV2 =
                (features & MaterialGiV2Feature.MaterialTransport) != 0;
            GiEmissiveMeshSampling =
                (features & MaterialGiV2Feature.EmissiveMeshSampling) != 0;
            GiFarFieldMaterialV2 =
                (features & MaterialGiV2Feature.FarFieldMaterial) != 0;
        }

        public int DdgiEmissiveTriangleBudget
        {
            get => _ddgiEmissiveTriangleBudget;
            set => _ddgiEmissiveTriangleBudget = Clamp(
                value,
                1,
                MaxDdgiEmissiveTriangleBudget);
        }
        public bool DdgiProbeClassificationEnabled { get; set; } = true;
        public bool DdgiProbeRelocationEnabled { get; set; } = true;
        public bool DdgiProbeL1MetadataEnabled { get; set; } = true;
        public bool DdgiCameraRelativeEnabled { get; set; } = true;
        public bool DdgiAdaptiveBudgetingEnabled { get; set; } = true;
        public bool DdgiThinWallPolicyEnabled { get; set; } = true;
        public bool DdgiAsyncComputeEnabled { get; set; } = true;
        /// <summary>
        /// Evaluates glTF alpha-mask cutoffs at ray-query candidates. This keeps
        /// curtains, fences, and cutout foliage from becoming opaque DDGI walls.
        /// Disabling it restores the conservative opaque approximation for A/B
        /// performance investigation only.
        /// </summary>
        public bool DdgiAlphaMaskedTransportEnabled { get; set; } = true;
        /// <summary>
        /// Authority for Simple-DDGI scheduling. GPU-resident scheduling is the
        /// default production path; CpuReference remains available as the
        /// explicit compatibility and fallback mode.
        /// </summary>
        public SimpleDdgiSchedulerMode SimpleDdgiSchedulerMode { get; set; } =
            SimpleDdgiSchedulerMode.GpuResident;
        /// <summary>
        /// Consecutive healthy CPU-fallback frames required before a latched
        /// GPU scheduler may bootstrap a fresh arena and regain authority.
        /// </summary>
        public int SimpleDdgiSchedulerReentryStableFrameCount
        {
            get => _simpleDdgiSchedulerReentryStableFrameCount;
            set => _simpleDdgiSchedulerReentryStableFrameCount =
                Math.Clamp(value, 1, 3_600);
        }
        /// <summary>
        /// Selects identity, qualification-shadow, or authoritative near-ring
        /// payload paging. Ultra enables the qualified sparse path. High stays
        /// dense because its complete authored cardinality fits the tier budget
        /// and must not inherit sparse feedback faults or coarse-ring fallback.
        /// </summary>
        public SimpleDdgiProbeResidencyMode SimpleDdgiProbeResidencyMode
        {
            get => _simpleDdgiProbeResidencyMode;
            set => _simpleDdgiProbeResidencyMode = value.Sanitize();
        }
        /// <summary>Fixed sparse near-ring physical page capacity.</summary>
        public int SimpleDdgiSparsePhysicalPageBudget
        {
            get => _simpleDdgiSparsePhysicalPageBudget;
            set => _simpleDdgiSparsePhysicalPageBudget = Math.Clamp(
                value,
                0,
                MaxSimpleDdgiTotalProbeCount / 8);
        }
        /// <summary>
        /// Lowest explicit page capacity permitted by Degrade admission. A plan
        /// below this value is rejected before Vulkan allocation.
        /// </summary>
        public int SimpleDdgiSparseMinimumPhysicalPageBudget
        {
            get => _simpleDdgiSparseMinimumPhysicalPageBudget;
            set => _simpleDdgiSparseMinimumPhysicalPageBudget = Math.Clamp(
                value,
                0,
                MaxSimpleDdgiTotalProbeCount / 8);
        }
        public int SimpleDdgiSparseRetentionFrames
        {
            get => _simpleDdgiSparseRetentionFrames;
            set => _simpleDdgiSparseRetentionFrames = Math.Clamp(value, 1, 3_600);
        }
        public int SimpleDdgiSparseMaximumAdmissionsPerFrame
        {
            get => _simpleDdgiSparseMaximumAdmissionsPerFrame;
            set => _simpleDdgiSparseMaximumAdmissionsPerFrame = Math.Clamp(
                value,
                1,
                MaxSimpleDdgiTotalProbeCount / 8);
        }
        public int SimpleDdgiSparseMaximumReceiverFeedbackRequests
        {
            get => _simpleDdgiSparseMaximumReceiverFeedbackRequests;
            set => _simpleDdgiSparseMaximumReceiverFeedbackRequests = Math.Clamp(
                value,
                0,
                MaxSimpleDdgiTotalProbeCount);
        }
        public int SimpleDdgiSparseInactiveRetryFrames
        {
            get => _simpleDdgiSparseInactiveRetryFrames;
            set => _simpleDdgiSparseInactiveRetryFrames = Math.Clamp(
                value,
                1,
                36_000);
        }
        public bool SimpleDdgiSharedMemoryBlendEnabled { get; set; } = true;
        public bool SimpleDdgiClassificationSchedulingEnabled { get; set; } = true;
        /// <summary>
        /// Reorders eligible maintenance work by delayed measured error per
        /// predicted cost. Hard quotas, source cohorts, latency bounds, and
        /// the complete-field certificate remain unchanged.
        /// </summary>
        public bool SimpleDdgiCostAwareSchedulingEnabled { get; set; } = true;
        /// <summary>
        /// Schema-v9 compatibility alias for delayed receiver feedback.
        /// New code selects a versioned mode; setting this alias can only
        /// request the legacy packed reference, never silently promote the
        /// ExactCompacted ABI.
        /// </summary>
        public bool SimpleDdgiReceiverContributionFeedbackEnabled
        {
            get => SimpleDdgiReceiverFeedbackMode !=
                SimpleDdgiReceiverFeedbackMode.Off;
            set => SimpleDdgiReceiverFeedbackMode = value
                ? SimpleDdgiReceiverFeedbackMode.LegacyPackedReference
                : SimpleDdgiReceiverFeedbackMode.Off;
        }
        /// <summary>
        /// Loads a checksummed, exact-identity prior for certified dense
        /// background volumes and refreshes it after live tail certification.
        /// Cached records never mark source geometry or lighting generations
        /// current; the ordinary scheduler remains authoritative.
        /// </summary>
        public bool SimpleDdgiPersistentWarmStartEnabled { get; set; } = true;
        /// <summary>
        /// Prioritizes probes reached by measured transport residuals before
        /// the generation-frozen complete sweep. Sparse propagation never
        /// replaces or loosens the 2.5% tail certificate.
        /// </summary>
        public bool SimpleDdgiSparseResidualPropagationEnabled { get; set; } = true;
        /// <summary>
        /// Enables the bounded pre-forward cache-only relight transaction for
        /// visible near-ring probes. Geometry/topology changes always remain on
        /// the ordinary post-forward path.
        /// </summary>
        public bool SimpleDdgiUrgentRelightEnabled { get; set; } = true;
        public int SimpleDdgiUrgentRelightProbeBudget
        {
            get => _simpleDdgiUrgentRelightProbeBudget;
            set => _simpleDdgiUrgentRelightProbeBudget = Math.Clamp(
                value,
                0,
                SimpleDdgiUrgentRelightPolicy.MaximumProbeBudget);
        }
        /// <summary>
        /// Selects the packed source-cache record organization. Auto uses
        /// delayed completed trace classifications and a hysteretic admission
        /// gate; forced hot/cold remains useful for locked A/B captures.
        /// </summary>
        public SimpleDdgiSourceCacheLayoutMode SimpleDdgiSourceCacheLayoutMode { get; set; } =
            SimpleDdgiSourceCacheLayoutMode.Auto;
        public bool SimpleDdgiClassificationReadbackEnabled { get; set; } = true;
        public bool SimpleDdgiAdaptiveHysteresisEnabled { get; set; } = true;
        public bool SimpleDdgiLightingDirtyBoostEnabled { get; set; } = true;
        public bool SimpleDdgiDynamicGeometryDirtyBoostEnabled { get; set; } = true;
        public bool SimpleDdgiAdaptiveRaysEnabled { get; set; } = true;
        /// <summary>
        /// Uses the V2 source-cache/Jacobi transport path.  V2 separates source
        /// tracing from recursive bounce solve and publishes only completed
        /// irradiance generations.  Disable only for an explicit V1 rollback.
        /// </summary>
        public bool SimpleDdgiTransportV2Enabled { get; set; } = true;
        /// <summary>
        /// Enables explicitly authored zero-thickness diffuse transmission in
        /// Simple DDGI. Keep this separate from raster blend/opacity policy so
        /// production captures can run deterministic opaque-versus-thin A/Bs.
        /// </summary>
        public bool SimpleDdgiThinSurfaceTransmissionEnabled { get; set; }
        /// <summary>
        /// Applies the automatic density policy to camera-relative rings.
        /// Authored volumes remain independent scene ownership declarations.
        /// </summary>
        public bool SimpleDdgiAutomaticProbeDensityEnabled { get; set; } = true;
        /// <summary>
        /// Uses the support-aware DDGI gather result in forward shading.  This is the
        /// production path: invalid, fresh, and exposed probe slots contribute no support
        /// and the environment owns the missing energy.
        /// </summary>
        public bool SimpleDdgiStructuredGatherEnabled { get; set; } = true;
        /// <summary>
        /// Selects the exact receiver oracle or a cache mode. Surface-aware
        /// spatial caching remains an explicit qualification candidate until
        /// its correctness and total-cost promotion gates pass. Runtime
        /// capability/resource failures always resolve back to exact.
        /// </summary>
        public SimpleDdgiReceiverCacheMode SimpleDdgiReceiverCacheMode
        {
            get => _simpleDdgiReceiverCacheMode;
            set => _simpleDdgiReceiverCacheMode = value.Sanitize();
        }
        public SimpleDdgiLayoutAdmissionMode SimpleDdgiLayoutAdmissionMode { get; set; } = SimpleDdgiLayoutAdmissionMode.Degrade;
        /// <summary>
        /// Uses SH projection for irradiance while retaining the reference
        /// ray-by-texel visibility estimator. High and ultra tiers keep the full
        /// reference irradiance path as well; reduced irradiance is reserved for
        /// explicitly performance-constrained tiers.
        /// </summary>
        public bool SimpleDdgiReducedBlendEnabled { get; set; }
        /// <summary>
        /// Enables the sampled-image mirror of the canonical Simple-DDGI SSBO
        /// atlases. The SSBO remains the writer and safety fallback; high and
        /// ultra tiers request this path, while the manager admits it only when
        /// the configured DDGI memory budget can safely accommodate it.
        /// </summary>
        public bool SimpleDdgiSampledAtlasEnabled { get; set; } = true;
        /// <summary>
        /// Selects whole-volume image coverage. ReceiverRelevant mirrors authored
        /// receiver volumes plus near/mid rings and uses the canonical SSBO for
        /// every excluded volume and octahedral seam.
        /// </summary>
        public SimpleDdgiSampledAtlasCoverageMode SimpleDdgiSampledAtlasCoverageMode { get; set; } =
            SimpleDdgiSampledAtlasCoverageMode.ReceiverRelevant;
        /// <summary>
        /// Selects the versioned source-cache/ray-scratch storage contract. A
        /// change is a cold resource-generation transition, never reinterpretation.
        /// </summary>
        public SimpleDdgiStoragePackingMode SimpleDdgiStoragePackingMode { get; set; } =
            SimpleDdgiStoragePackingMode.Packed;
        /// <summary>
        /// Compatibility property for the former approximate early-out. Runtime
        /// ownership composition is conservative and therefore always uses 1.0.
        /// </summary>
        public float SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold
        {
            get => _simpleDdgiSecondVolumeOwnershipEarlyOutThreshold;
            set => _simpleDdgiSecondVolumeOwnershipEarlyOutThreshold = 1.0f;
        }

        /// <summary>
        /// Diagnostic A/B control that evaluates the pre-gate far-field sky
        /// visibility path even when its radiometric weight is negligible. The
        /// result is still multiplied by the exact fallback weight, so this
        /// changes work attribution without changing intended output.
        /// </summary>
        public bool SimpleDdgiForceLegacyFarFieldFallbackEvaluation { get; set; }
        /// <summary>
        /// Keeps camera-relative rings in a shared toroidal physical layout so normal
        /// scrolling invalidates only exposed cells and never copies atlas data.
        /// </summary>
        public bool SimpleDdgiToroidalScrollingEnabled { get; set; } = true;
        /// <summary>
        /// Maps bounded dirty regions to affected simple-DDGI probe cells instead of
        /// promoting the entire world after every dynamic change.
        /// </summary>
        public bool SimpleDdgiRegionalInvalidationEnabled { get; set; } = true;
        /// <summary>
        /// Consumes producer-authored scene/light/material/VFX edits instead of
        /// comparing every object signature on ordinary frames.
        /// </summary>
        public bool SimpleDdgiMutationJournalEnabled { get; set; } = true;
        /// <summary>
        /// Runs the full-scene comparison path in parallel and records dirty
        /// region mismatches. Intended for qualification captures only.
        /// </summary>
        public bool SimpleDdgiMutationJournalValidationOracleEnabled { get; set; }
        public bool SimpleDdgiFogEnabled { get; set; } = true;
        /// <summary>
        /// Requests the B5 directional fog experiment. It remains fail-closed
        /// until a production L2 incident-radiance sidecar and froxel consumer
        /// are both present; the existing irradiance tint is not relabeled.
        /// </summary>
        public bool SimpleDdgiDirectionalFogEnabled { get; set; } = true;
        /// <summary>
        /// Schema-v9 compatibility alias.  It maps an old true value to the
        /// explicit EXT four-state experiment and never to AutoQualified.
        /// </summary>
        public bool DdgiOpacityMicromapExperimentEnabled
        {
            get => DdgiOpacityMicromapMode != DdgiOpacityMicromapMode.Off;
            set => DdgiOpacityMicromapMode = value
                ? DdgiOpacityMicromapMode.ExtFourStateExperiment
                : DdgiOpacityMicromapMode.Off;
        }
        public bool DdgiRayTracingPipelineExperimentEnabled { get; set; }
        /// <summary>Schema-v9 compatibility alias for C3's histogram experiment.</summary>
        public bool SimpleDdgiDirectionalRayGuidingExperimentEnabled
        {
            get => SimpleDdgiDirectionalGuidingMode !=
                SimpleDdgiDirectionalGuidingMode.Off;
            set => SimpleDdgiDirectionalGuidingMode = value
                ? SimpleDdgiDirectionalGuidingMode.PerProbeHistogramExperiment
                : SimpleDdgiDirectionalGuidingMode.Off;
        }
        /// <summary>Schema-v9 compatibility alias for C4's world-cache experiment.</summary>
        public bool DdgiTaggedCausticCacheExperimentEnabled
        {
            get => GiCausticMode != GiCausticMode.Off;
            set => GiCausticMode = value
                ? GiCausticMode.WorldCacheExperiment
                : GiCausticMode.Off;
        }
        /// <summary>Schema-v9 compatibility alias for C5's Hi-Z experiment.</summary>
        public bool SimpleDdgiNearFieldResidualExperimentEnabled
        {
            get => SimpleDdgiNearFieldResidualMode !=
                SimpleDdgiNearFieldResidualMode.Off;
            set => SimpleDdgiNearFieldResidualMode = value
                ? SimpleDdgiNearFieldResidualMode.HiZHalfResolutionExperiment
                : SimpleDdgiNearFieldResidualMode.Off;
        }

        public SimpleDdgiReceiverFeedbackMode SimpleDdgiReceiverFeedbackMode
        {
            get => _simpleDdgiReceiverFeedbackMode;
            set => _simpleDdgiReceiverFeedbackMode = Enum.IsDefined(value)
                ? value
                : SimpleDdgiReceiverFeedbackMode.Off;
        }

        public DdgiOpacityMicromapMode DdgiOpacityMicromapMode
        {
            get => _ddgiOpacityMicromapMode;
            set => _ddgiOpacityMicromapMode = Enum.IsDefined(value)
                ? value
                : DdgiOpacityMicromapMode.Off;
        }

        public SimpleDdgiDirectionalGuidingMode SimpleDdgiDirectionalGuidingMode
        {
            get => _simpleDdgiDirectionalGuidingMode;
            set => _simpleDdgiDirectionalGuidingMode = Enum.IsDefined(value)
                ? value
                : SimpleDdgiDirectionalGuidingMode.Off;
        }

        public GiCausticMode GiCausticMode
        {
            get => _giCausticMode;
            set => _giCausticMode = Enum.IsDefined(value)
                ? value
                : GiCausticMode.Off;
        }

        public SimpleDdgiNearFieldResidualMode SimpleDdgiNearFieldResidualMode
        {
            get => _simpleDdgiNearFieldResidualMode;
            set => _simpleDdgiNearFieldResidualMode = Enum.IsDefined(value)
                ? value
                : SimpleDdgiNearFieldResidualMode.Off;
        }

        public SimpleDdgiNearFieldResidualQualityPreset
            SimpleDdgiNearFieldResidualQualityPreset
        {
            get => _simpleDdgiNearFieldResidualQualityPreset;
            set => _simpleDdgiNearFieldResidualQualityPreset = Enum.IsDefined(value)
                ? value
                : DefaultSimpleDdgiNearFieldResidualQualityPreset;
        }

        /// <summary>
        /// Enables the bounded developer controls below. AutoQualified still
        /// cannot exceed the limits carried by its admitted evidence entry.
        /// </summary>
        public bool SimpleDdgiNearFieldResidualAdvancedOverridesEnabled { get; set; }

        /// <summary>
        /// Enables the bounded C5 tile scheduler/checkerboard path. Explicit
        /// HiZAdaptive presets may use it directly; AutoQualified still requires
        /// authenticated candidate evidence.
        /// </summary>
        public bool SimpleDdgiNearFieldResidualLocalAdaptiveSchedulingEnabled
            { get; set; }

        public float SimpleDdgiNearFieldResidualMaximumTraceDistanceMeters
        {
            get => _simpleDdgiNearFieldResidualMaximumTraceDistanceMeters;
            set => _simpleDdgiNearFieldResidualMaximumTraceDistanceMeters =
                Clamp(value, 2.0f, 16.0f);
        }

        public int SimpleDdgiNearFieldResidualRaysPerPixel
        {
            get => _simpleDdgiNearFieldResidualRaysPerPixel;
            set => _simpleDdgiNearFieldResidualRaysPerPixel =
                Math.Clamp(value, 1, 4);
        }

        public int SimpleDdgiNearFieldResidualFilterIterationCount
        {
            get => _simpleDdgiNearFieldResidualFilterIterationCount;
            set => _simpleDdgiNearFieldResidualFilterIterationCount =
                Math.Clamp(value, 0, 4);
        }

        public float SimpleDdgiNearFieldResidualIntensity
        {
            get => _simpleDdgiNearFieldResidualIntensity;
            set => _simpleDdgiNearFieldResidualIntensity =
                Clamp(value, 0.0f, 2.0f);
        }

        /// <summary>
        /// Stable device/driver/shader/content evidence identifier needed when
        /// an AutoQualified selection is evaluated.  An invalid or absent ID
        /// is intentionally normalized to empty so admission fails closed.
        /// </summary>
        public string SimpleDdgiReceiverFeedbackQualificationId
        {
            get => _simpleDdgiReceiverFeedbackQualificationId;
            set => _simpleDdgiReceiverFeedbackQualificationId =
                NormalizeAdvancedGiQualificationId(value);
        }

        public string DdgiOpacityMicromapQualificationId
        {
            get => _ddgiOpacityMicromapQualificationId;
            set => _ddgiOpacityMicromapQualificationId =
                NormalizeAdvancedGiQualificationId(value);
        }

        public string SimpleDdgiDirectionalGuidingQualificationId
        {
            get => _simpleDdgiDirectionalGuidingQualificationId;
            set => _simpleDdgiDirectionalGuidingQualificationId =
                NormalizeAdvancedGiQualificationId(value);
        }

        public string GiCausticQualificationId
        {
            get => _giCausticQualificationId;
            set => _giCausticQualificationId =
                NormalizeAdvancedGiQualificationId(value);
        }

        public string SimpleDdgiNearFieldResidualQualificationId
        {
            get => _simpleDdgiNearFieldResidualQualificationId;
            set => _simpleDdgiNearFieldResidualQualificationId =
                NormalizeAdvancedGiQualificationId(value);
        }

        private static string NormalizeAdvancedGiQualificationId(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            string normalized = value.Trim();
            // A qualification ID is a compact evidence hash, never a free-form
            // report.  Reject rather than truncate so a copied partial hash can
            // never accidentally match an authorization record.
            return normalized.Length <= 256 ? normalized : string.Empty;
        }

        public bool SimpleDdgiParticlesEnabled { get; set; } = true;
        /// <summary>
        /// Schema-v8 compatibility alias. New code must use the typed directional
        /// radiance and glossy transport modes. Setting this alias performs the
        /// old explicit experimental opt-in; reading it reports that same intent.
        /// </summary>
        public bool SimpleDdgiRoughSpecularEnabled
        {
            get => SimpleDdgiDirectionalRadianceMode != SimpleDdgiDirectionalRadianceMode.Off &&
                SimpleDdgiGlossyTransportMode != SimpleDdgiGlossyTransportMode.Off;
            set
            {
                SimpleDdgiDirectionalRadianceMode = value
                    ? SimpleDdgiDirectionalRadianceMode.L2
                    : SimpleDdgiDirectionalRadianceMode.Off;
                SimpleDdgiGlossyTransportMode = value
                    ? SimpleDdgiGlossyTransportMode.ReceiverOnly
                    : SimpleDdgiGlossyTransportMode.Off;
            }
        }

        /// <summary>Non-persisted per-device qualification for the additions.</summary>
        public DdgiContentRolloutPolicy ContentDependentRollout { get; } = new();

        /// <summary>Set only when an older settings schema needed semantic migration.</summary>
        public string? ContentDependentSettingsMigrationDiagnostic { get; internal set; }
        public bool FarFieldSkyVisibilityEnabled { get; set; } = true;
        public bool FarFieldSunShadowEnabled { get; set; } = true;
        public bool FarFieldClipmapEnabled { get; set; } = true;
        /// <summary>
        /// Uses fixed-resolution, world-keyed physical pages instead of one
        /// scene-sized voxel cube.  The legacy clipmap remains available only for
        /// A/B validation and emergency rollback.
        /// </summary>
        public bool FarFieldPagedEnabled { get; set; } = true;
        /// <summary>
        /// Requests the independently versioned dominant-surface material
        /// payload. Runtime consumers use the effective policy-authorized
        /// switch; the unqualified default retains V1 occupancy/RGB8.
        /// </summary>
        public bool GiFarFieldMaterialV2 { get; set; }
        public bool FarFieldForceAll { get; set; }
        /// <summary>
        /// Restricts static GI ray-query geometry to a camera-relative working set
        /// with deterministic eviction. Dynamic render objects remain detailed and
        /// authoritative while present.
        /// </summary>
        public bool StreamedGiAccelerationStructuresEnabled { get; set; } = true;
        /// <summary>
        /// Explicit standalone local quality overrides. Empty by default; the
        /// camera-relative near, mid, and far rings provide normal world coverage.
        /// </summary>
        public IList<SimpleDdgiAuthoredVolume> SimpleDdgiAuthoredVolumes { get; } = new List<SimpleDdgiAuthoredVolume>();

        public float SimpleDdgiProbeSpacing
        {
            get => _simpleDdgiProbeSpacing;
            set => _simpleDdgiProbeSpacing = Clamp(value, 0.25f, 8.0f);
        }

        public int SimpleDdgiRingCount
        {
            get => _simpleDdgiRingCount;
            set => _simpleDdgiRingCount = Clamp(value, 0, 3);
        }

        public float SimpleDdgiRingBaseSpacing
        {
            get => _simpleDdgiRingBaseSpacing;
            set => _simpleDdgiRingBaseSpacing = Clamp(value, 0.25f, 16.0f);
        }

        public float SimpleDdgiRingSpacingMultiplier
        {
            get => _simpleDdgiRingSpacingMultiplier;
            set => _simpleDdgiRingSpacingMultiplier = Clamp(value, 1.25f, 8.0f);
        }

        /// <summary>
        /// Flax-style camera-forward placement bias for camera-relative DDGI
        /// rings. Zero keeps the camera at the lattice centre; the default 0.6
        /// retains roughly one fifth of a cardinal ring behind the camera and
        /// spends the remaining coverage in front of it.
        /// </summary>
        public float SimpleDdgiViewForwardPlacementFraction
        {
            get => _simpleDdgiViewForwardPlacementFraction;
            set => _simpleDdgiViewForwardPlacementFraction = Clamp(value, 0.0f, 1.0f);
        }

        /// <summary>
        /// Explicit vertical placement policy for simple-DDGI rings.  The
        /// receiver-anchored mode keeps tall, authored receiver coverage stable
        /// while horizontal camera-relative scrolling remains active.
        /// </summary>
        public SimpleDdgiVerticalRingPolicy SimpleDdgiVerticalRingPolicy
        {
            get => _simpleDdgiVerticalRingPolicy;
            set => _simpleDdgiVerticalRingPolicy = Enum.IsDefined(value)
                ? value
                : SimpleDdgiVerticalRingPolicy.CameraRelativeWithHysteresis;
        }

        /// <summary>
        /// World-space vertical centre used when <see cref="SimpleDdgiVerticalRingPolicy"/>
        /// is receiver anchored.  It is intentionally a scene-profile value,
        /// not a camera-derived offset.
        /// </summary>
        public float SimpleDdgiReceiverVerticalAnchor
        {
            get => _simpleDdgiReceiverVerticalAnchor;
            set => _simpleDdgiReceiverVerticalAnchor = Clamp(value, -2048.0f, 2048.0f);
        }

        /// <summary>
        /// Fraction of a ring's vertical lattice extent that remains as a
        /// recenter dead-zone in camera-relative-with-hysteresis mode.
        /// </summary>
        public float SimpleDdgiVerticalRecenterHysteresisFraction
        {
            get => _simpleDdgiVerticalRecenterHysteresisFraction;
            set => _simpleDdgiVerticalRecenterHysteresisFraction = Clamp(value, 0.0f, 0.49f);
        }

        /// <summary>
        /// Compatibility broadcast for callers that intentionally want one grid for every
        /// camera-relative ring. New production configurations use the explicit near/mid/far
        /// grid settings below.
        /// </summary>
        public int SimpleDdgiRingGridSizeX
        {
            get => _simpleDdgiNearRingGridSizeX;
            set
            {
                int clamped = Clamp(value, 2, MaxSimpleDdgiProbeCountX);
                _simpleDdgiNearRingGridSizeX = clamped;
                _simpleDdgiMidRingGridSizeX = clamped;
                _simpleDdgiFarRingGridSizeX = clamped;
            }
        }

        /// <summary>Compatibility broadcast; see <see cref="SimpleDdgiRingGridSizeX"/>.</summary>
        public int SimpleDdgiRingGridSizeY
        {
            get => _simpleDdgiNearRingGridSizeY;
            set
            {
                int clamped = Clamp(value, 2, MaxSimpleDdgiProbeCountY);
                _simpleDdgiNearRingGridSizeY = clamped;
                _simpleDdgiMidRingGridSizeY = clamped;
                _simpleDdgiFarRingGridSizeY = clamped;
            }
        }

        /// <summary>Compatibility broadcast; see <see cref="SimpleDdgiRingGridSizeX"/>.</summary>
        public int SimpleDdgiRingGridSizeZ
        {
            get => _simpleDdgiNearRingGridSizeZ;
            set
            {
                int clamped = Clamp(value, 2, MaxSimpleDdgiProbeCountZ);
                _simpleDdgiNearRingGridSizeZ = clamped;
                _simpleDdgiMidRingGridSizeZ = clamped;
                _simpleDdgiFarRingGridSizeZ = clamped;
            }
        }

        public int SimpleDdgiNearRingGridSizeX
        {
            get => _simpleDdgiNearRingGridSizeX;
            set => _simpleDdgiNearRingGridSizeX = Clamp(value, 2, MaxSimpleDdgiProbeCountX);
        }

        public int SimpleDdgiNearRingGridSizeY
        {
            get => _simpleDdgiNearRingGridSizeY;
            set => _simpleDdgiNearRingGridSizeY = Clamp(value, 2, MaxSimpleDdgiProbeCountY);
        }

        public int SimpleDdgiNearRingGridSizeZ
        {
            get => _simpleDdgiNearRingGridSizeZ;
            set => _simpleDdgiNearRingGridSizeZ = Clamp(value, 2, MaxSimpleDdgiProbeCountZ);
        }

        public int SimpleDdgiMidRingGridSizeX
        {
            get => _simpleDdgiMidRingGridSizeX;
            set => _simpleDdgiMidRingGridSizeX = Clamp(value, 2, MaxSimpleDdgiProbeCountX);
        }

        public int SimpleDdgiMidRingGridSizeY
        {
            get => _simpleDdgiMidRingGridSizeY;
            set => _simpleDdgiMidRingGridSizeY = Clamp(value, 2, MaxSimpleDdgiProbeCountY);
        }

        public int SimpleDdgiMidRingGridSizeZ
        {
            get => _simpleDdgiMidRingGridSizeZ;
            set => _simpleDdgiMidRingGridSizeZ = Clamp(value, 2, MaxSimpleDdgiProbeCountZ);
        }

        public int SimpleDdgiFarRingGridSizeX
        {
            get => _simpleDdgiFarRingGridSizeX;
            set => _simpleDdgiFarRingGridSizeX = Clamp(value, 2, MaxSimpleDdgiProbeCountX);
        }

        public int SimpleDdgiFarRingGridSizeY
        {
            get => _simpleDdgiFarRingGridSizeY;
            set => _simpleDdgiFarRingGridSizeY = Clamp(value, 2, MaxSimpleDdgiProbeCountY);
        }

        public int SimpleDdgiFarRingGridSizeZ
        {
            get => _simpleDdgiFarRingGridSizeZ;
            set => _simpleDdgiFarRingGridSizeZ = Clamp(value, 2, MaxSimpleDdgiProbeCountZ);
        }

        /// <summary>
        /// Enables the bounded B3 fine-probe overlay. Receiver ownership still
        /// requires V2 tail certification; disabling this switch allocates no
        /// refinement probes.
        /// </summary>
        public bool SimpleDdgiRefinementBricksEnabled
        {
            get => _simpleDdgiRefinementBricksEnabled;
            set => _simpleDdgiRefinementBricksEnabled = value;
        }

        public int SimpleDdgiRefinementMaximumBricks
        {
            get => _simpleDdgiRefinementMaximumBricks;
            set => _simpleDdgiRefinementMaximumBricks = Clamp(
                value,
                0,
                SimpleDdgiRefinementBrickPool.MaximumCapacity);
        }

        public int SimpleDdgiRefinementGridSizeX
        {
            get => _simpleDdgiRefinementGridSizeX;
            set => _simpleDdgiRefinementGridSizeX = Clamp(value, 2, 16);
        }

        public int SimpleDdgiRefinementGridSizeY
        {
            get => _simpleDdgiRefinementGridSizeY;
            set => _simpleDdgiRefinementGridSizeY = Clamp(value, 2, 12);
        }

        public int SimpleDdgiRefinementGridSizeZ
        {
            get => _simpleDdgiRefinementGridSizeZ;
            set => _simpleDdgiRefinementGridSizeZ = Clamp(value, 2, 16);
        }

        public float SimpleDdgiRefinementSpacingScale
        {
            get => _simpleDdgiRefinementSpacingScale;
            set => _simpleDdgiRefinementSpacingScale = Clamp(value, 0.25f, 1.0f);
        }

        public int SimpleDdgiRefinementRetentionFrames
        {
            get => _simpleDdgiRefinementRetentionFrames;
            set => _simpleDdgiRefinementRetentionFrames = Clamp(value, 0, 3_600);
        }

        public float SimpleDdgiRefinementMinimumEmissiveLuminanceNits
        {
            get => _simpleDdgiRefinementMinimumEmissiveLuminanceNits;
            set => _simpleDdgiRefinementMinimumEmissiveLuminanceNits =
                Clamp(value, 0f, 1_000_000f);
        }

        public float SimpleDdgiRefinementMaximumEmitterAreaSquareMeters
        {
            get => _simpleDdgiRefinementMaximumEmitterAreaSquareMeters;
            set => _simpleDdgiRefinementMaximumEmitterAreaSquareMeters =
                Clamp(value, 0.001f, 1_000_000f);
        }

        /// <summary>
        /// Requests the independently admitted B4 conservative near-occluder
        /// sidecar. Failure to admit it never removes canonical probes or the
        /// ordinary visibility-moment fallback.
        /// </summary>
        public bool SimpleDdgiNearVisibilitySidecarEnabled
        {
            get => _simpleDdgiNearVisibilitySidecarEnabled;
            set => _simpleDdgiNearVisibilitySidecarEnabled = value;
        }

        public ulong SimpleDdgiNearVisibilitySidecarMemoryBudgetBytes
        {
            get => _simpleDdgiNearVisibilitySidecarMemoryBudgetBytes;
            set => _simpleDdgiNearVisibilitySidecarMemoryBudgetBytes = Math.Clamp(
                value,
                0UL,
                256UL * 1024UL * 1024UL);
        }

        public int SimpleDdgiRaysPerProbe
        {
            get => _simpleDdgiRaysPerProbe;
            set => _simpleDdgiRaysPerProbe = Clamp(value, 1, MaxSimpleDdgiRaysPerProbe);
        }

        public int SimpleDdgiMaintenanceRaysPerProbe
        {
            get => Math.Min(_simpleDdgiMaintenanceRaysPerProbe, _simpleDdgiRaysPerProbe);
            set => _simpleDdgiMaintenanceRaysPerProbe = Clamp(value, 1, MaxSimpleDdgiRaysPerProbe);
        }

        public int SimpleDdgiNearFullRaysPerProbe
        {
            get => _simpleDdgiNearFullRaysPerProbe;
            set => _simpleDdgiNearFullRaysPerProbe = Clamp(value, 1, MaxSimpleDdgiRaysPerProbe);
        }

        public int SimpleDdgiMidFullRaysPerProbe
        {
            get => _simpleDdgiMidFullRaysPerProbe;
            set => _simpleDdgiMidFullRaysPerProbe = Clamp(value, 1, MaxSimpleDdgiRaysPerProbe);
        }

        public int SimpleDdgiFarFullRaysPerProbe
        {
            get => _simpleDdgiFarFullRaysPerProbe;
            set => _simpleDdgiFarFullRaysPerProbe = Clamp(value, 1, MaxSimpleDdgiRaysPerProbe);
        }

        public int SimpleDdgiNearMaintenanceRaysPerProbe
        {
            get => Math.Min(_simpleDdgiNearMaintenanceRaysPerProbe, _simpleDdgiNearFullRaysPerProbe);
            set => _simpleDdgiNearMaintenanceRaysPerProbe = Clamp(value, 1, MaxSimpleDdgiRaysPerProbe);
        }

        public int SimpleDdgiMidMaintenanceRaysPerProbe
        {
            get => Math.Min(_simpleDdgiMidMaintenanceRaysPerProbe, _simpleDdgiMidFullRaysPerProbe);
            set => _simpleDdgiMidMaintenanceRaysPerProbe = Clamp(value, 1, MaxSimpleDdgiRaysPerProbe);
        }

        public int SimpleDdgiFarMaintenanceRaysPerProbe
        {
            get => Math.Min(_simpleDdgiFarMaintenanceRaysPerProbe, _simpleDdgiFarFullRaysPerProbe);
            set => _simpleDdgiFarMaintenanceRaysPerProbe = Clamp(value, 1, MaxSimpleDdgiRaysPerProbe);
        }

        public int SimpleDdgiNearMinimumUpdateQuota
        {
            get => _simpleDdgiNearMinimumUpdateQuota;
            set => _simpleDdgiNearMinimumUpdateQuota = Clamp(value, 0, MaxSimpleDdgiTotalProbeCount);
        }

        public int SimpleDdgiMidMinimumUpdateQuota
        {
            get => _simpleDdgiMidMinimumUpdateQuota;
            set => _simpleDdgiMidMinimumUpdateQuota = Clamp(value, 0, MaxSimpleDdgiTotalProbeCount);
        }

        public int SimpleDdgiFarMinimumUpdateQuota
        {
            get => _simpleDdgiFarMinimumUpdateQuota;
            set => _simpleDdgiFarMinimumUpdateQuota = Clamp(value, 0, MaxSimpleDdgiTotalProbeCount);
        }

        public int SimpleDdgiNearMaximumUpdateQuota
        {
            get => Math.Max(_simpleDdgiNearMaximumUpdateQuota, _simpleDdgiNearMinimumUpdateQuota);
            set => _simpleDdgiNearMaximumUpdateQuota = Clamp(value, 0, MaxSimpleDdgiTotalProbeCount);
        }

        public int SimpleDdgiMidMaximumUpdateQuota
        {
            get => Math.Max(_simpleDdgiMidMaximumUpdateQuota, _simpleDdgiMidMinimumUpdateQuota);
            set => _simpleDdgiMidMaximumUpdateQuota = Clamp(value, 0, MaxSimpleDdgiTotalProbeCount);
        }

        public int SimpleDdgiFarMaximumUpdateQuota
        {
            get => Math.Max(_simpleDdgiFarMaximumUpdateQuota, _simpleDdgiFarMinimumUpdateQuota);
            set => _simpleDdgiFarMaximumUpdateQuota = Clamp(value, 0, MaxSimpleDdgiTotalProbeCount);
        }

        /// <summary>-1 uses compact material statistics instead of texture samples.</summary>
        public int SimpleDdgiNearMaterialTextureMaxCascade
        {
            get => _simpleDdgiNearMaterialTextureMaxCascade;
            set => _simpleDdgiNearMaterialTextureMaxCascade = Clamp(value, -1, MaxSimpleDdgiMaterialTextureCascade - 1);
        }

        public int SimpleDdgiMidMaterialTextureMaxCascade
        {
            get => _simpleDdgiMidMaterialTextureMaxCascade;
            set => _simpleDdgiMidMaterialTextureMaxCascade = Clamp(value, -1, MaxSimpleDdgiMaterialTextureCascade - 1);
        }

        public int SimpleDdgiFarMaterialTextureMaxCascade
        {
            get => _simpleDdgiFarMaterialTextureMaxCascade;
            set => _simpleDdgiFarMaterialTextureMaxCascade = Clamp(value, -1, MaxSimpleDdgiMaterialTextureCascade - 1);
        }

        public SimpleDdgiLocalLightSamplingMode SimpleDdgiLocalLightSamplingMode
        {
            get => _simpleDdgiLocalLightSamplingMode;
            set => _simpleDdgiLocalLightSamplingMode = Enum.IsDefined(value)
                ? value
                : SimpleDdgiLocalLightSamplingMode.Auto;
        }

        public SimpleDdgiDirectionalRadianceMode SimpleDdgiDirectionalRadianceMode
        {
            get => _simpleDdgiDirectionalRadianceMode;
            set => _simpleDdgiDirectionalRadianceMode = Enum.IsDefined(value)
                ? value
                : SimpleDdgiDirectionalRadianceMode.Off;
        }

        public SimpleDdgiGlossyTransportMode SimpleDdgiGlossyTransportMode
        {
            get => _simpleDdgiGlossyTransportMode;
            set => _simpleDdgiGlossyTransportMode = Enum.IsDefined(value)
                ? value
                : SimpleDdgiGlossyTransportMode.Off;
        }

        public DdgiSkinnedGeometryMode DdgiSkinnedGeometryMode
        {
            get => _ddgiSkinnedGeometryMode;
            set => _ddgiSkinnedGeometryMode = Enum.IsDefined(value)
                ? value
                : DdgiSkinnedGeometryMode.Excluded;
        }

        public DdgiTransparentGeometryMode DdgiTransparentGeometryMode
        {
            get => _ddgiTransparentGeometryMode;
            set => _ddgiTransparentGeometryMode = Enum.IsDefined(value)
                ? value
                : DdgiTransparentGeometryMode.MaskOnly;
        }

        public DdgiFoliageGeometryMode DdgiFoliageGeometryMode
        {
            get => _ddgiFoliageGeometryMode;
            set => _ddgiFoliageGeometryMode = Enum.IsDefined(value)
                ? value
                : DdgiFoliageGeometryMode.Excluded;
        }

        public int SimpleDdgiNearLocalLightSamplesPerHit
        {
            get => _simpleDdgiNearLocalLightSamplesPerHit;
            set => _simpleDdgiNearLocalLightSamplesPerHit = Clamp(
                value,
                0,
                MaxSimpleDdgiLocalLightSamplesPerHit);
        }

        public int SimpleDdgiMidLocalLightSamplesPerHit
        {
            get => _simpleDdgiMidLocalLightSamplesPerHit;
            set => _simpleDdgiMidLocalLightSamplesPerHit = Clamp(
                value,
                0,
                MaxSimpleDdgiLocalLightSamplesPerHit);
        }

        public int SimpleDdgiFarLocalLightSamplesPerHit
        {
            get => _simpleDdgiFarLocalLightSamplesPerHit;
            set => _simpleDdgiFarLocalLightSamplesPerHit = Clamp(
                value,
                0,
                MaxSimpleDdgiLocalLightSamplesPerHit);
        }

        public int SimpleDdgiExactLocalLightThreshold
        {
            get => _simpleDdgiExactLocalLightThreshold;
            set => _simpleDdgiExactLocalLightThreshold = Clamp(
                value,
                0,
                MaxSimpleDdgiExactLocalLightThreshold);
        }

        /// <summary>
        /// Probability assigned to the exact uniform-over-eligible-leaves
        /// component of the tree proposal. This preserves non-zero support.
        /// </summary>
        public float SimpleDdgiLightTreeUniformMixtureProbability
        {
            get => _simpleDdgiLightTreeUniformMixtureProbability;
            set => _simpleDdgiLightTreeUniformMixtureProbability = Clamp(value, 0.001f, 0.25f);
        }

        public int SimpleDdgiLightTreeMaximumRefitAge
        {
            get => _simpleDdgiLightTreeMaximumRefitAge;
            set => _simpleDdgiLightTreeMaximumRefitAge = Clamp(value, 1, 4_096);
        }

        public ulong DdgiDynamicBlasMemoryBudgetBytes
        {
            get => _ddgiDynamicBlasMemoryBudgetBytes;
            set => _ddgiDynamicBlasMemoryBudgetBytes = Math.Min(
                value,
                MaxDdgiDynamicAccelerationStructureBudgetBytes);
        }

        public ulong DdgiDynamicBlasScratchBudgetBytes
        {
            get => _ddgiDynamicBlasScratchBudgetBytes;
            set => _ddgiDynamicBlasScratchBudgetBytes = Math.Min(
                value,
                MaxDdgiDynamicAccelerationStructureBudgetBytes);
        }

        public int DdgiDynamicBlasBuildsPerFrame
        {
            get => _ddgiDynamicBlasBuildsPerFrame;
            set => _ddgiDynamicBlasBuildsPerFrame = Clamp(value, 0, 1_024);
        }

        public int DdgiDynamicBlasPrimitivesPerFrame
        {
            get => _ddgiDynamicBlasPrimitivesPerFrame;
            set => _ddgiDynamicBlasPrimitivesPerFrame = Clamp(value, 0, 16_777_216);
        }

        public int DdgiFoliageProxyTriangleBudget
        {
            get => _ddgiFoliageProxyTriangleBudget;
            set => _ddgiFoliageProxyTriangleBudget = Clamp(value, 0, 4_000_000);
        }

        public int DdgiFoliageProxyUpdateCadenceFrames
        {
            get => _ddgiFoliageProxyUpdateCadenceFrames;
            set => _ddgiFoliageProxyUpdateCadenceFrames = Clamp(value, 1, 1_024);
        }

        public int DdgiTransparencyCandidateLimit
        {
            get => _ddgiTransparencyCandidateLimit;
            set => _ddgiTransparencyCandidateLimit = Clamp(value, 1, 256);
        }

        public int DdgiTransparencyLayerLimit
        {
            get => _ddgiTransparencyLayerLimit;
            set => _ddgiTransparencyLayerLimit = Clamp(value, 1, 64);
        }

        public int DdgiDecalCandidateLimit
        {
            get => _ddgiDecalCandidateLimit;
            set => _ddgiDecalCandidateLimit = Clamp(
                value,
                0,
                DdgiGeometryParticipation.ProductionDecalCandidateLimit);
        }

        public ulong SimpleDdgiDirectionalRadianceMemoryBudgetBytes
        {
            get => _simpleDdgiDirectionalRadianceMemoryBudgetBytes;
            set => _simpleDdgiDirectionalRadianceMemoryBudgetBytes = Math.Min(
                value,
                MaxSimpleDdgiDirectionalRadianceBudgetBytes);
        }

        public float SimpleDdgiRoughSpecularMinimumRoughness
        {
            get => _simpleDdgiRoughSpecularMinimumRoughness;
            set
            {
                _simpleDdgiRoughSpecularMinimumRoughness = Clamp(value, 0f, 1f);
                if (_simpleDdgiRoughSpecularFullWeightRoughness <
                    _simpleDdgiRoughSpecularMinimumRoughness)
                {
                    _simpleDdgiRoughSpecularFullWeightRoughness =
                        _simpleDdgiRoughSpecularMinimumRoughness;
                }
            }
        }

        public float SimpleDdgiRoughSpecularFullWeightRoughness
        {
            get => Math.Max(
                _simpleDdgiRoughSpecularFullWeightRoughness,
                _simpleDdgiRoughSpecularMinimumRoughness);
            set => _simpleDdgiRoughSpecularFullWeightRoughness = Clamp(value, 0f, 1f);
        }

        /// <summary>
        /// Schema-v8 compatibility aliases. They now carry the local stochastic
        /// sample count and are retained only for one migration schema.
        /// </summary>
        public int SimpleDdgiNearMaxShadedLights
        {
            get => SimpleDdgiNearLocalLightSamplesPerHit;
            set
            {
                _simpleDdgiNearMaxShadedLights = Clamp(value, 0, 64);
                SimpleDdgiNearLocalLightSamplesPerHit = value;
            }
        }

        public int SimpleDdgiMidMaxShadedLights
        {
            get => SimpleDdgiMidLocalLightSamplesPerHit;
            set
            {
                _simpleDdgiMidMaxShadedLights = Clamp(value, 0, 64);
                SimpleDdgiMidLocalLightSamplesPerHit = value;
            }
        }

        public int SimpleDdgiFarMaxShadedLights
        {
            get => SimpleDdgiFarLocalLightSamplesPerHit;
            set
            {
                _simpleDdgiFarMaxShadedLights = Clamp(value, 0, 64);
                SimpleDdgiFarLocalLightSamplesPerHit = value;
            }
        }

        public float SimpleDdgiHysteresis
        {
            get => _simpleDdgiHysteresis;
            set => _simpleDdgiHysteresis = Clamp(value, 0.0f, 0.995f);
        }

        public float SimpleDdgiHysteresisChangeThreshold
        {
            get => _simpleDdgiHysteresisChangeThreshold;
            set => _simpleDdgiHysteresisChangeThreshold = Clamp(value, 0.001f, 4.0f);
        }

        public float SimpleDdgiHysteresisStepThreshold
        {
            get => Math.Max(_simpleDdgiHysteresisStepThreshold, _simpleDdgiHysteresisChangeThreshold);
            set => _simpleDdgiHysteresisStepThreshold = Clamp(value, 0.001f, 8.0f);
        }

        public int SimpleDdgiLightingDirtyFrameCount
        {
            get => _simpleDdgiLightingDirtyFrameCount;
            set => _simpleDdgiLightingDirtyFrameCount = Clamp(value, 0, 300);
        }

        public int SimpleDdgiStableMaintenanceUpdateCount
        {
            get => _simpleDdgiStableMaintenanceUpdateCount;
            set => _simpleDdgiStableMaintenanceUpdateCount = Clamp(value, 1, 64);
        }

        public float SimpleDdgiStableMaintenanceEmaThreshold
        {
            get => _simpleDdgiStableMaintenanceEmaThreshold;
            set => _simpleDdgiStableMaintenanceEmaThreshold = Clamp(value, 0.0f, 1.0f);
        }

        /// <summary>
        /// Jacobi relaxation used when a completed V2 transport iteration is
        /// published.  Values below one damp Monte-Carlo noise without trapping
        /// indirect energy in the old 0.97 temporal-history regime.
        /// </summary>
        public float SimpleDdgiTransportSolverRelaxation
        {
            get => _simpleDdgiTransportSolverRelaxation;
            set => _simpleDdgiTransportSolverRelaxation = Clamp(value, 0.05f, 1.0f);
        }

        /// <summary>Conservative diffuse reflectance ceiling for recursive transport.</summary>
        public float SimpleDdgiTransportAlbedoClamp
        {
            get => _simpleDdgiTransportAlbedoClamp;
            set => _simpleDdgiTransportAlbedoClamp = Clamp(value, 0.50f, 0.99f);
        }

        /// <summary>
        /// Relative complete-field tail tolerance for the certified V2
        /// transport operator. The absolute floor is fixed at 0.0001.
        /// </summary>
        public float SimpleDdgiTransportTailRelativeTolerance
        {
            get => _simpleDdgiTransportTailRelativeTolerance;
            set => _simpleDdgiTransportTailRelativeTolerance = float.IsFinite(value)
                ? Clamp(value, 0.0f, 1.0f)
                : 0.025f;
        }

        /// <summary>
        /// Number of deterministic cached-source sweeps in a production solve
        /// epoch. Source rays are never retraced for these sweeps.
        /// </summary>
        public int SimpleDdgiTransportAcceleratedSweepCount
        {
            get => _simpleDdgiTransportAcceleratedSweepCount;
            set => _simpleDdgiTransportAcceleratedSweepCount = Clamp(value, 1, 4);
        }

        /// <summary>Enables red-black accelerated cached-source transport sweeps.</summary>
        public bool SimpleDdgiTransportAccelerationEnabled
        {
            get => _simpleDdgiTransportAccelerationEnabled;
            set => _simpleDdgiTransportAccelerationEnabled = value;
        }

        /// <summary>
        /// Requires a generation-frozen complete-field audit before V2 reports
        /// convergence. Disabling it is an explicit diagnostics/rollback mode.
        /// </summary>
        public bool SimpleDdgiTransportTailCertificationEnabled
        {
            get => _simpleDdgiTransportTailCertificationEnabled;
            set => _simpleDdgiTransportTailCertificationEnabled = value;
        }

        /// <summary>
        /// Legacy alias retained for source compatibility and migration. It is
        /// no longer a local residual gate; it maps directly to the complete
        /// field tail tolerance.
        /// </summary>
        [Obsolete("Use SimpleDdgiTransportTailRelativeTolerance. This alias is not a convergence gate.")]
        public float SimpleDdgiTransportResidualThreshold
        {
            get => SimpleDdgiTransportTailRelativeTolerance;
            set => SimpleDdgiTransportTailRelativeTolerance = value;
        }

        /// <summary>
        /// Minimum cached-source solve generations required before convergence can
        /// retire a probe. The legacy property name is retained for settings-file
        /// compatibility.
        /// </summary>
        public int SimpleDdgiTransportMaximumSolverGenerations
        {
            get => _simpleDdgiTransportMaximumSolverGenerations;
            set => _simpleDdgiTransportMaximumSolverGenerations = Clamp(value, 1, 64);
        }

        /// <summary>
        /// Periodic source-ray refresh interval for otherwise static physical
        /// probe slots.  Lighting edits and remapped cells refresh immediately.
        /// </summary>
        public int SimpleDdgiTransportSourceRefreshFrames
        {
            get => _simpleDdgiTransportSourceRefreshFrames;
            set => _simpleDdgiTransportSourceRefreshFrames = Clamp(value, 1, 4_096);
        }

        /// <summary>
        /// Multiplies near-ring spacing (with a gentler mid-ring ramp) when V2
        /// automatic density is active.  Physical probe count stays bounded;
        /// density is obtained by concentrating the camera clipmaps.
        /// </summary>
        public float SimpleDdgiAutomaticProbeDensityScale
        {
            get => _simpleDdgiAutomaticProbeDensityScale;
            set => _simpleDdgiAutomaticProbeDensityScale = Clamp(value, 0.45f, 1.0f);
        }

        public float SimpleDdgiNormalBias
        {
            get => _simpleDdgiNormalBias;
            set => _simpleDdgiNormalBias = Clamp(value, 0.0f, 1.0f);
        }

        public float SimpleDdgiViewBias
        {
            get => _simpleDdgiViewBias;
            set => _simpleDdgiViewBias = Clamp(value, 0.0f, 2.0f);
        }

        /// <summary>
        /// Absolute cap for the combined normal/view interpolation displacement.
        /// This remains independent of probe spacing so far rings cannot turn a
        /// spacing-relative authoring value into a multi-metre world-space bias.
        /// </summary>
        public float SimpleDdgiMaximumWorldBiasMeters
        {
            get => _simpleDdgiMaximumWorldBiasMeters;
            set => _simpleDdgiMaximumWorldBiasMeters = Clamp(value, 0.004f, 1.0f);
        }

        /// <summary>
        /// Conservative minimum architectural thickness used to cap Simple-DDGI
        /// interpolation bias. The shader permits at most one quarter of this
        /// value, leaving a substantial safety margin for thin walls.
        /// </summary>
        public float SimpleDdgiArchitecturalThicknessMeters
        {
            get => _simpleDdgiArchitecturalThicknessMeters;
            set => _simpleDdgiArchitecturalThicknessMeters = Clamp(value, 0.008f, 4.0f);
        }

        public int SimpleDdgiProbeUpdatesPerFrame
        {
            get => _simpleDdgiProbeUpdatesPerFrame;
            set => _simpleDdgiProbeUpdatesPerFrame = Clamp(value, 0, MaxSimpleDdgiTotalProbeCount);
        }

        public int FarFieldClipmapResolution
        {
            get => _farFieldClipmapResolution;
            set => _farFieldClipmapResolution = Clamp(value, 16, MaxFarFieldClipmapResolution);
        }

        public float FarFieldStartDistance
        {
            get => _farFieldStartDistance;
            set => _farFieldStartDistance = Clamp(value, 0.0f, 512.0f);
        }

        public int FarFieldMaxTraceSteps
        {
            get => _farFieldMaxTraceSteps;
            set => _farFieldMaxTraceSteps = Clamp(value, 1, 2048);
        }

        /// <summary>Voxel resolution of one physical far-field page.</summary>
        public int FarFieldPageResolution
        {
            get => _farFieldPageResolution;
            set => _farFieldPageResolution = Clamp(value, 16, 128);
        }

        /// <summary>Number of geometric far-field cascades represented by the page cache.</summary>
        public int FarFieldCascadeCount
        {
            get => _farFieldCascadeCount;
            set => _farFieldCascadeCount = Clamp(value, 1, 4);
        }

        /// <summary>Hard cap on resident physical pages across all cascades.</summary>
        public int FarFieldResidentPageBudget
        {
            get => _farFieldResidentPageBudget;
            set => _farFieldResidentPageBudget = Clamp(value, 1, 256);
        }

        /// <summary>Maximum pages rebuilt each frame; bounds streaming work and hitches.</summary>
        public int FarFieldPageUpdatesPerFrame
        {
            get => _farFieldPageUpdatesPerFrame;
            set => _farFieldPageUpdatesPerFrame = Clamp(value, 1, 16);
        }

        /// <summary>World-page radius requested around the camera in each cascade.</summary>
        public int FarFieldPageRequestRadius
        {
            get => _farFieldPageRequestRadius;
            set => _farFieldPageRequestRadius = Clamp(value, 0, 4);
        }

        /// <summary>Stable world-space voxel size of cascade zero.</summary>
        public float FarFieldBaseVoxelSize
        {
            get => _farFieldBaseVoxelSize;
            set => _farFieldBaseVoxelSize = Clamp(value, 0.125f, 64.0f);
        }

        /// <summary>Geometric voxel-size multiplier between far-field cascades.</summary>
        public float FarFieldCascadeVoxelScale
        {
            get => _farFieldCascadeVoxelScale;
            set => _farFieldCascadeVoxelScale = Clamp(value, 1.25f, 8.0f);
        }

        /// <summary>Hard memory ceiling for far-field page pool, distance, and scratch buffers.</summary>
        public ulong FarFieldMemoryBudgetBytes
        {
            get => _farFieldMemoryBudgetBytes;
            set => _farFieldMemoryBudgetBytes = Clamp(value, 8UL * 1024UL * 1024UL, 512UL * 1024UL * 1024UL);
        }

        /// <summary>Hard cap for active BLAS and TLAS allocations used by GI ray queries.</summary>
        public ulong GiAccelerationStructureMemoryBudgetBytes
        {
            get => _giAccelerationStructureMemoryBudgetBytes;
            set => _giAccelerationStructureMemoryBudgetBytes = Clamp(value, 16UL * 1024UL * 1024UL, 2048UL * 1024UL * 1024UL);
        }

        /// <summary>Maximum camera distance at which static batch instances enter the GI TLAS.</summary>
        public float GiAccelerationStructureStaticResidentDistance
        {
            get => _giAccelerationStructureStaticResidentDistance;
            set => _giAccelerationStructureStaticResidentDistance = Clamp(value, 1.0f, 100_000.0f);
        }

        /// <summary>Upper bound on static batch instances selected for a GI TLAS frame.</summary>
        public int GiAccelerationStructureMaximumStaticInstances
        {
            get => _giAccelerationStructureMaximumStaticInstances;
            set => _giAccelerationStructureMaximumStaticInstances = Clamp(value, 0, 65_536);
        }

        /// <summary>Frames an unused BLAS remains resident before normal streaming eviction.</summary>
        public int GiAccelerationStructureEvictionGraceFrames
        {
            get => _giAccelerationStructureEvictionGraceFrames;
            set => _giAccelerationStructureEvictionGraceFrames = Clamp(value, 0, 3_600);
        }

        public int DdgiProbeUpdatePrimaryRayBudget
        {
            get => _ddgiProbeUpdatePrimaryRayBudget;
            set => _ddgiProbeUpdatePrimaryRayBudget = Clamp(value, 0, MaxDdgiProbeUpdatePrimaryRayBudget);
        }

        public int DdgiMaxShadedLights
        {
            get => _ddgiMaxShadedLights;
            set => _ddgiMaxShadedLights = Clamp(value, 0, 64);
        }

        public int DdgiMaterialTextureMaxCascade
        {
            get => _ddgiMaterialTextureMaxCascade;
            set => _ddgiMaterialTextureMaxCascade = Clamp(value, -1, MaxSimpleDdgiMaterialTextureCascade - 1);
        }

        public ulong DdgiAtlasMemoryBudgetBytes
        {
            get => _ddgiAtlasMemoryBudgetBytes;
            set => _ddgiAtlasMemoryBudgetBytes = Clamp(value, 1UL * 1024UL * 1024UL, 2048UL * 1024UL * 1024UL);
        }

        public float DdgiThinWallLeakClampStrength
        {
            get => _ddgiThinWallLeakClampStrength;
            set => _ddgiThinWallLeakClampStrength = Clamp(value, 0.0f, 1.0f);
        }

        public float DdgiSelfShadowBiasScale
        {
            get => _ddgiSelfShadowBiasScale;
            set => _ddgiSelfShadowBiasScale = Clamp(value, 0.25f, 4.0f);
        }

        public float EffectiveDdgiAdaptiveBudgetTimeMilliseconds => DdgiQualityTier switch
        {
            DdgiQualityTier.DdgiLow => 0.75f,
            DdgiQualityTier.DdgiMedium => 1.0f,
            DdgiQualityTier.DdgiUltra => 4.0f,
            _ => 3.0f
        };

        public float ResolutionScale
        {
            get => _resolutionScale;
            set => _resolutionScale = value <= 0.375f ? 0.25f : value <= 0.75f ? 0.5f : 1.0f;
        }

        public float MaxBounceDistance
        {
            get => _maxBounceDistance;
            set => _maxBounceDistance = Clamp(value, 0.1f, 100.0f);
        }

        public bool TemporalEnabled { get; set; } = true;
        public bool DenoiserEnabled { get; set; } = true;

        public float HistoryResponsiveness
        {
            get => _historyResponsiveness;
            set => _historyResponsiveness = Clamp(value, 0.01f, 1.0f);
        }

        public float NormalRejectionThreshold
        {
            get => _normalRejectionThreshold;
            set => _normalRejectionThreshold = Clamp(value, 0.0f, 1.0f);
        }

        public float DepthRejectionThreshold
        {
            get => _depthRejectionThreshold;
            set => _depthRejectionThreshold = Clamp(value, 0.0001f, 10.0f);
        }

        public float LeakClampStrength
        {
            get => _leakClampStrength;
            set => _leakClampStrength = Clamp(value, 0.0f, 1.0f);
        }

        public bool EffectiveUseDdgi => !EmergencyGiFallbackEnabled &&
            Enabled &&
            UseDdgi &&
            Mode == GlobalIlluminationMode.Ddgi;

        public bool EffectiveUseRayQueryBackend => !EmergencyGiFallbackEnabled &&
            Enabled &&
            UseRayQueryBackend &&
            EffectiveUseDdgi;

        public DdgiContentFeature ConfiguredContentDependentFeatures
        {
            get
            {
                DdgiContentFeature features = DdgiContentFeature.None;
                if (SimpleDdgiNearLocalLightSamplesPerHit > 0 ||
                    SimpleDdgiMidLocalLightSamplesPerHit > 0 ||
                    SimpleDdgiFarLocalLightSamplesPerHit > 0)
                {
                    features |= DdgiContentFeature.ManyLightSampling;
                }

                if (DdgiSkinnedGeometryMode != DdgiSkinnedGeometryMode.Excluded)
                    features |= DdgiContentFeature.CurrentPoseGeometry;
                if (DdgiTransparentGeometryMode != DdgiTransparentGeometryMode.MaskOnly)
                    features |= DdgiContentFeature.TransparentGeometry;
                if (DdgiFoliageGeometryMode != DdgiFoliageGeometryMode.Excluded)
                    features |= DdgiContentFeature.FoliageGeometry;
                // Directional incident radiance is a producer contract.  Fog
                // consumes it independently from the optional glossy surface
                // receiver, so do not couple publication to glossy admission.
                if (SimpleDdgiDirectionalRadianceMode !=
                    SimpleDdgiDirectionalRadianceMode.Off)
                {
                    features |= DdgiContentFeature.DirectionalRadiance;
                }

                if (SimpleDdgiGlossyTransportMode is
                    SimpleDdgiGlossyTransportMode.OneBounce or
                    SimpleDdgiGlossyTransportMode.RecursiveCertified)
                {
                    features |= DdgiContentFeature.OneBounceGlossyTransport;
                }

                if (SimpleDdgiGlossyTransportMode ==
                    SimpleDdgiGlossyTransportMode.RecursiveCertified)
                {
                    features |= DdgiContentFeature.RecursiveGlossyTransport;
                }

                return features;
            }
        }

        public DdgiContentFeature ActiveContentDependentFeatures
        {
            get
            {
                if (!EffectiveUseDdgi)
                    return DdgiContentFeature.None;

                DdgiContentFeature active = ContentDependentRollout.Resolve(
                    ConfiguredContentDependentFeatures);
                if (SimpleDdgiLocalLightSamplingMode ==
                        SimpleDdgiLocalLightSamplingMode.LegacyTopKReference &&
                    !ContentDependentRollout.ValidationReferenceModesAuthorized)
                {
                    active &= ~DdgiContentFeature.ManyLightSampling;
                }

                if (SimpleDdgiDirectionalRadianceMode ==
                        SimpleDdgiDirectionalRadianceMode.L1Reference &&
                    !ContentDependentRollout.ValidationReferenceModesAuthorized)
                {
                    active &= ~DdgiContentFeature.DirectionalRadiance;
                }

                return active;
            }
        }

        public bool EffectiveSimpleDdgiManyLightSamplingEnabled =>
            (ActiveContentDependentFeatures & DdgiContentFeature.ManyLightSampling) != 0;

        /// <summary>
        /// Resolves the skinned representation used by DDGI. During the short
        /// frozen tail audit, stable-identity pose invalidations are coalesced
        /// and applied immediately after the immutable cached solve completes;
        /// the live BLAS therefore does not weaken the certificate's operator.
        /// </summary>
        public DdgiSkinnedGeometryMode EffectiveDdgiSkinnedGeometryMode
        {
            get
            {
                if ((ActiveContentDependentFeatures &
                        DdgiContentFeature.CurrentPoseGeometry) == 0)
                {
                    return DdgiSkinnedGeometryMode.Excluded;
                }

                return DdgiSkinnedGeometryMode;
            }
        }

        public DdgiTransparentGeometryMode EffectiveDdgiTransparentGeometryMode =>
            (ActiveContentDependentFeatures & DdgiContentFeature.TransparentGeometry) != 0
                ? DdgiTransparentGeometryMode
                : DdgiTransparentGeometryMode.MaskOnly;

        public DdgiFoliageGeometryMode EffectiveDdgiFoliageGeometryMode =>
            (ActiveContentDependentFeatures & DdgiContentFeature.FoliageGeometry) != 0
                ? DdgiFoliageGeometryMode
                : DdgiFoliageGeometryMode.Excluded;

        public SimpleDdgiDirectionalRadianceMode EffectiveSimpleDdgiDirectionalRadianceMode =>
            (ActiveContentDependentFeatures & DdgiContentFeature.DirectionalRadiance) != 0
                ? SimpleDdgiDirectionalRadianceMode
                : SimpleDdgiDirectionalRadianceMode.Off;

        public SimpleDdgiGlossyTransportMode EffectiveSimpleDdgiGlossyTransportMode
        {
            get
            {
                if ((ActiveContentDependentFeatures & DdgiContentFeature.DirectionalRadiance) == 0)
                    return SimpleDdgiGlossyTransportMode.Off;
                if (SimpleDdgiGlossyTransportMode is
                        SimpleDdgiGlossyTransportMode.OneBounce or
                        SimpleDdgiGlossyTransportMode.RecursiveCertified &&
                    (ActiveContentDependentFeatures &
                        DdgiContentFeature.OneBounceGlossyTransport) == 0)
                {
                    return SimpleDdgiGlossyTransportMode.ReceiverOnly;
                }

                if (SimpleDdgiGlossyTransportMode ==
                        SimpleDdgiGlossyTransportMode.RecursiveCertified &&
                    (ActiveContentDependentFeatures &
                        DdgiContentFeature.RecursiveGlossyTransport) == 0)
                {
                    return SimpleDdgiGlossyTransportMode.OneBounce;
                }

                return SimpleDdgiGlossyTransportMode;
            }
        }

        public void UseQualifiedContentDependentBaseline() =>
            ContentDependentRollout.UseQualifiedLegacyBaseline();

        public void EnableContentDependentFeaturesForConformance(
            DdgiContentFeature features = DdgiContentFeature.All,
            bool authorizeReferenceModes = false) =>
            ContentDependentRollout.EnableForConformance(features, authorizeReferenceModes);

        public void ApplyContentDependentReleaseQualification(DdgiContentFeature features) =>
            ContentDependentRollout.ApplyReleaseQualification(features);

        public void ApplyDdgiQualityTier(DdgiQualityTier tier)
        {
            DdgiQualityTier = tier;
            DdgiEmissiveTriangleBudget = tier switch
            {
                DdgiQualityTier.DdgiLow => 512,
                DdgiQualityTier.DdgiMedium => 2_048,
                _ => MaxDdgiEmissiveTriangleBudget
            };
            DdgiAdaptiveBudgetingEnabled = true;
            DdgiProbeClassificationEnabled = true;
            DdgiProbeRelocationEnabled = true;
            DdgiProbeL1MetadataEnabled = true;
            DdgiCameraRelativeEnabled = true;
            SimpleDdgiStructuredGatherEnabled = true;
            SimpleDdgiLayoutAdmissionMode = SimpleDdgiLayoutAdmissionMode.Degrade;
            SimpleDdgiTransportV2Enabled = true;
            SimpleDdgiAutomaticProbeDensityEnabled = true;
            SimpleDdgiTransportSolverRelaxation = 0.70f;
            SimpleDdgiTransportAlbedoClamp = 0.95f;
            SimpleDdgiTransportTailRelativeTolerance = 0.025f;
            SimpleDdgiTransportAcceleratedSweepCount = 2;
            bool productionGiTier = tier != DdgiQualityTier.DdgiLow;
            // Production tiers run the bounded red-black solve and retain the
            // canonical Jacobi path as the automatic safety fallback. Low is
            // the explicit no-GI profile and does not reserve accelerated or
            // receiver-side resources.
            SimpleDdgiTransportAccelerationEnabled = productionGiTier;
            SimpleDdgiTransportTailCertificationEnabled = true;
            SimpleDdgiReceiverCacheMode = productionGiTier
                ? DefaultSimpleDdgiReceiverCacheMode
                : SimpleDdgiReceiverCacheMode.Exact;
            SimpleDdgiReceiverFeedbackMode = productionGiTier
                ? DefaultSimpleDdgiReceiverFeedbackMode
                : SimpleDdgiReceiverFeedbackMode.Off;
            DdgiOpacityMicromapMode = productionGiTier
                ? DefaultDdgiOpacityMicromapMode
                : DdgiOpacityMicromapMode.Off;
            SimpleDdgiDirectionalGuidingMode = productionGiTier
                ? DefaultSimpleDdgiDirectionalGuidingMode
                : SimpleDdgiDirectionalGuidingMode.Off;
            GiCausticMode = productionGiTier
                ? DefaultGiCausticMode
                : GiCausticMode.Off;
            bool directionalTier = productionGiTier;
            bool highTier = tier is
                DdgiQualityTier.DdgiHigh or DdgiQualityTier.DdgiUltra;
            SimpleDdgiRefinementBricksEnabled = productionGiTier;
            SimpleDdgiRefinementMaximumBricks = tier == DdgiQualityTier.DdgiUltra
                ? 4
                : tier == DdgiQualityTier.DdgiHigh
                    ? 2
                    : productionGiTier
                        ? 1
                        : 0;
            SimpleDdgiNearVisibilitySidecarEnabled = productionGiTier;
            SimpleDdgiNearVisibilitySidecarMemoryBudgetBytes = tier ==
                DdgiQualityTier.DdgiUltra
                    ? 96UL * 1024UL * 1024UL
                    : tier == DdgiQualityTier.DdgiHigh
                        ? 64UL * 1024UL * 1024UL
                        : productionGiTier
                            ? 32UL * 1024UL * 1024UL
                            : 0UL;
            SimpleDdgiSourceCacheLayoutMode =
                SimpleDdgiSourceCacheLayoutMode.Auto;
            SimpleDdgiTransportMaximumSolverGenerations = 8;
            SimpleDdgiViewForwardPlacementFraction = 0.6f;
            SimpleDdgiVerticalRingPolicy = SimpleDdgiVerticalRingPolicy.CameraRelativeWithHysteresis;
            SimpleDdgiVerticalRecenterHysteresisFraction = 0.25f;
            SimpleDdgiReducedBlendEnabled = tier is DdgiQualityTier.DdgiLow or DdgiQualityTier.DdgiMedium;
            SimpleDdgiSampledAtlasEnabled = tier is DdgiQualityTier.DdgiHigh or DdgiQualityTier.DdgiUltra;
            SimpleDdgiSampledAtlasCoverageMode = SimpleDdgiSampledAtlasEnabled
                ? SimpleDdgiSampledAtlasCoverageMode.ReceiverRelevant
                : SimpleDdgiSampledAtlasCoverageMode.Disabled;
            // Packed storage is the production representation for every tier.
            // Legacy/Validate and full-canonical mirroring remain explicit
            // rollback and qualification overrides.
            SimpleDdgiStoragePackingMode = SimpleDdgiStoragePackingMode.Packed;
            SimpleDdgiProbeResidencyMode = tier == DdgiQualityTier.DdgiUltra
                ? SimpleDdgiProbeResidencyMode.SparseNearRing
                : SimpleDdgiProbeResidencyMode.Dense;
            SimpleDdgiSparsePhysicalPageBudget = tier switch
            {
                DdgiQualityTier.DdgiUltra => 1_440,
                _ => 0
            };
            SimpleDdgiSparseMinimumPhysicalPageBudget = tier switch
            {
                DdgiQualityTier.DdgiUltra => 1_152,
                _ => 0
            };
            SimpleDdgiSparseRetentionFrames = tier == DdgiQualityTier.DdgiUltra
                ? 150
                : 120;
            SimpleDdgiSparseMaximumAdmissionsPerFrame =
                tier == DdgiQualityTier.DdgiUltra ? 96 : 64;
            SimpleDdgiSparseMaximumReceiverFeedbackRequests =
                tier == DdgiQualityTier.DdgiUltra ? 4_096 : 2_048;
            SimpleDdgiSparseInactiveRetryFrames = 300;
            SimpleDdgiToroidalScrollingEnabled = true;
            SimpleDdgiRegionalInvalidationEnabled = true;
            SimpleDdgiMutationJournalEnabled = true;
            SimpleDdgiLocalLightSamplingMode = SimpleDdgiLocalLightSamplingMode.Auto;
            // Per-fragment directional SH evaluation bypasses the production
            // low-frequency receiver cache. High-class volumetric fog now
            // consumes the same production L2 publication through a clustered
            // HG phase query; ordinary opaque receivers may still select their
            // cheaper cache path independently.
            SimpleDdgiDirectionalRadianceMode = directionalTier
                ? SimpleDdgiDirectionalRadianceMode.L2
                : SimpleDdgiDirectionalRadianceMode.Off;
            SimpleDdgiGlossyTransportMode = directionalTier
                ? SimpleDdgiGlossyTransportMode.RecursiveCertified
                : SimpleDdgiGlossyTransportMode.Off;
            SimpleDdgiDirectionalFogEnabled = directionalTier;
            // Current-pose transport remains gated by the authenticated
            // content-dependent rollout. Tail audits use geometry-epoch
            // snapshots and never claim a stale pose as generation-current.
            DdgiSkinnedGeometryMode = tier is
                DdgiQualityTier.DdgiHigh or DdgiQualityTier.DdgiUltra
                    ? DdgiSkinnedGeometryMode.CurrentPose
                    : DdgiSkinnedGeometryMode.Excluded;
            DdgiTransparentGeometryMode = tier switch
            {
                DdgiQualityTier.DdgiUltra => DdgiTransparentGeometryMode.StochasticBlend,
                DdgiQualityTier.DdgiHigh => DdgiTransparentGeometryMode.MaskAndThin,
                _ => DdgiTransparentGeometryMode.MaskOnly
            };
            DdgiFoliageGeometryMode = tier switch
            {
                DdgiQualityTier.DdgiUltra =>
                    DdgiFoliageGeometryMode.AuthoredAndProceduralProxy,
                DdgiQualityTier.DdgiHigh =>
                    DdgiFoliageGeometryMode.AuthoredMeshOnly,
                _ => DdgiFoliageGeometryMode.Excluded
            };
            SimpleDdgiExactLocalLightThreshold = tier == DdgiQualityTier.DdgiUltra ? 12 : 8;
            SimpleDdgiDirectionalRadianceMemoryBudgetBytes = tier switch
            {
                DdgiQualityTier.DdgiUltra => 128UL * 1024UL * 1024UL,
                DdgiQualityTier.DdgiHigh => 64UL * 1024UL * 1024UL,
                DdgiQualityTier.DdgiMedium => 32UL * 1024UL * 1024UL,
                _ => 0UL
            };
            DdgiDynamicBlasMemoryBudgetBytes = tier switch
            {
                DdgiQualityTier.DdgiUltra => 512UL * 1024UL * 1024UL,
                DdgiQualityTier.DdgiHigh => 256UL * 1024UL * 1024UL,
                _ => 0UL
            };
            DdgiDynamicBlasScratchBudgetBytes = tier switch
            {
                DdgiQualityTier.DdgiUltra => 128UL * 1024UL * 1024UL,
                DdgiQualityTier.DdgiHigh => 64UL * 1024UL * 1024UL,
                _ => 0UL
            };
            DdgiDynamicBlasBuildsPerFrame = tier switch
            {
                DdgiQualityTier.DdgiUltra => 32,
                DdgiQualityTier.DdgiHigh => 16,
                _ => 0
            };
            DdgiFoliageProxyTriangleBudget = tier == DdgiQualityTier.DdgiUltra
                ? 500_000
                : 0;

            (DdgiProbeUpdatePrimaryRayBudget, DdgiMaxShadedLights, DdgiMaterialTextureMaxCascade, DdgiAtlasMemoryBudgetBytes) = tier switch
            {
                DdgiQualityTier.DdgiLow => (4_096, 2, -1, 64UL * 1024UL * 1024UL),
                DdgiQualityTier.DdgiMedium => (16_384, 4, 0, 128UL * 1024UL * 1024UL),
                DdgiQualityTier.DdgiUltra => (524_288, 16, 3, 384UL * 1024UL * 1024UL),
                _ => (262_144, 8, 1, 288UL * 1024UL * 1024UL)
            };

            ApplySimpleDdgiQualityTier(tier);
        }

        private void ApplySimpleDdgiQualityTier(DdgiQualityTier tier)
        {
            // The simple path has independent bounded resources.  These settings are
            // intentionally explicit rather than inheriting a fixed 20k+ probe layout
            // from whatever tier happened to be active previously.
            SimpleDdgiAdaptiveHysteresisEnabled = true;
            SimpleDdgiLightingDirtyBoostEnabled = true;
            SimpleDdgiAdaptiveRaysEnabled = true;
            SimpleDdgiClassificationSchedulingEnabled = true;
            SimpleDdgiClassificationReadbackEnabled = true;
            SimpleDdgiAutomaticProbeDensityScale = tier switch
            {
                DdgiQualityTier.DdgiLow => 0.90f,
                DdgiQualityTier.DdgiMedium => 0.80f,
                DdgiQualityTier.DdgiUltra => 0.60f,
                _ => 0.70f
            };
            SimpleDdgiTransportSourceRefreshFrames = tier switch
            {
                // Lower tiers have less per-frame solve throughput. These are
                // source-age floors, not minimum-generation proxies; the live
                // opportunity calculation may extend them for larger fields.
                DdgiQualityTier.DdgiLow => 4_096,
                DdgiQualityTier.DdgiMedium => 3_072,
                DdgiQualityTier.DdgiUltra => 1_536,
                _ => 2_048
            };

            switch (tier)
            {
                case DdgiQualityTier.DdgiLow:
                    SimpleDdgiRingCount = 2;
                    SimpleDdgiRingBaseSpacing = 1.75f;
                    SimpleDdgiRingSpacingMultiplier = 2.5f;
                    SimpleDdgiNearRingGridSizeX = 16;
                    SimpleDdgiNearRingGridSizeY = 8;
                    SimpleDdgiNearRingGridSizeZ = 16;
                    SimpleDdgiMidRingGridSizeX = 10;
                    SimpleDdgiMidRingGridSizeY = 6;
                    SimpleDdgiMidRingGridSizeZ = 10;
                    SimpleDdgiFarRingGridSizeX = 6;
                    SimpleDdgiFarRingGridSizeY = 4;
                    SimpleDdgiFarRingGridSizeZ = 6;
                    SimpleDdgiRaysPerProbe = 32;
                    SimpleDdgiMaintenanceRaysPerProbe = 8;
                    SimpleDdgiProbeUpdatesPerFrame = 128;
                    FarFieldClipmapResolution = 64;
                    FarFieldMaxTraceSteps = 96;
                    break;

                case DdgiQualityTier.DdgiMedium:
                    SimpleDdgiRingCount = 2;
                    SimpleDdgiRingBaseSpacing = 1.40f;
                    SimpleDdgiRingSpacingMultiplier = 2.75f;
                    SimpleDdgiNearRingGridSizeX = 22;
                    SimpleDdgiNearRingGridSizeY = 11;
                    SimpleDdgiNearRingGridSizeZ = 22;
                    SimpleDdgiMidRingGridSizeX = 14;
                    SimpleDdgiMidRingGridSizeY = 8;
                    SimpleDdgiMidRingGridSizeZ = 14;
                    SimpleDdgiFarRingGridSizeX = 10;
                    SimpleDdgiFarRingGridSizeY = 6;
                    SimpleDdgiFarRingGridSizeZ = 10;
                    SimpleDdgiRaysPerProbe = 64;
                    SimpleDdgiMaintenanceRaysPerProbe = 16;
                    SimpleDdgiProbeUpdatesPerFrame = 384;
                    FarFieldClipmapResolution = 96;
                    FarFieldMaxTraceSteps = 128;
                    break;

                case DdgiQualityTier.DdgiUltra:
                    SimpleDdgiRingCount = 3;
                    SimpleDdgiRingBaseSpacing = 1.0f;
                    SimpleDdgiRingSpacingMultiplier = 3.25f;
                    SimpleDdgiNearRingGridSizeX = 32;
                    SimpleDdgiNearRingGridSizeY = 16;
                    SimpleDdgiNearRingGridSizeZ = 32;
                    SimpleDdgiMidRingGridSizeX = 21;
                    SimpleDdgiMidRingGridSizeY = 12;
                    SimpleDdgiMidRingGridSizeZ = 21;
                    SimpleDdgiFarRingGridSizeX = 14;
                    SimpleDdgiFarRingGridSizeY = 10;
                    SimpleDdgiFarRingGridSizeZ = 14;
                    SimpleDdgiRaysPerProbe = 192;
                    SimpleDdgiMaintenanceRaysPerProbe = 48;
                    SimpleDdgiProbeUpdatesPerFrame = 3_072;
                    FarFieldClipmapResolution = 192;
                    FarFieldMaxTraceSteps = 256;
                    break;

                default:
                    SimpleDdgiRingCount = 3;
                    SimpleDdgiRingBaseSpacing = 1.25f;
                    SimpleDdgiRingSpacingMultiplier = 3.0f;
                    SimpleDdgiNearRingGridSizeX = 28;
                    SimpleDdgiNearRingGridSizeY = 14;
                    SimpleDdgiNearRingGridSizeZ = 28;
                    SimpleDdgiMidRingGridSizeX = 18;
                    SimpleDdgiMidRingGridSizeY = 10;
                    SimpleDdgiMidRingGridSizeZ = 18;
                    SimpleDdgiFarRingGridSizeX = 12;
                    SimpleDdgiFarRingGridSizeY = 8;
                    SimpleDdgiFarRingGridSizeZ = 12;
                    SimpleDdgiRaysPerProbe = 128;
                    SimpleDdgiMaintenanceRaysPerProbe = 32;
                    SimpleDdgiProbeUpdatesPerFrame = 2_048;
                    FarFieldClipmapResolution = 128;
                    FarFieldMaxTraceSteps = 192;
                    break;
            }

            ApplySimpleDdgiRingQualityTier(tier);
            ApplyFarFieldPageQualityTier(tier);
            ApplyGiAccelerationStructureQualityTier(tier);
        }

        private void ApplySimpleDdgiRingQualityTier(DdgiQualityTier tier)
        {
            switch (tier)
            {
                case DdgiQualityTier.DdgiLow:
                    SimpleDdgiNearFullRaysPerProbe = 32;
                    SimpleDdgiMidFullRaysPerProbe = 16;
                    SimpleDdgiFarFullRaysPerProbe = 8;
                    SimpleDdgiNearMaintenanceRaysPerProbe = 8;
                    SimpleDdgiMidMaintenanceRaysPerProbe = 4;
                    SimpleDdgiFarMaintenanceRaysPerProbe = 2;
                    SimpleDdgiNearMinimumUpdateQuota = 64;
                    SimpleDdgiMidMinimumUpdateQuota = 24;
                    SimpleDdgiFarMinimumUpdateQuota = 8;
                    SimpleDdgiNearMaximumUpdateQuota = 112;
                    SimpleDdgiMidMaximumUpdateQuota = 48;
                    SimpleDdgiFarMaximumUpdateQuota = 24;
                    SimpleDdgiNearMaterialTextureMaxCascade = 0;
                    SimpleDdgiMidMaterialTextureMaxCascade = -1;
                    SimpleDdgiFarMaterialTextureMaxCascade = -1;
                    SimpleDdgiNearLocalLightSamplesPerHit = 0;
                    SimpleDdgiMidLocalLightSamplesPerHit = 0;
                    SimpleDdgiFarLocalLightSamplesPerHit = 0;
                    break;

                case DdgiQualityTier.DdgiMedium:
                    SimpleDdgiNearFullRaysPerProbe = 64;
                    SimpleDdgiMidFullRaysPerProbe = 32;
                    SimpleDdgiFarFullRaysPerProbe = 16;
                    SimpleDdgiNearMaintenanceRaysPerProbe = 16;
                    SimpleDdgiMidMaintenanceRaysPerProbe = 8;
                    SimpleDdgiFarMaintenanceRaysPerProbe = 4;
                    SimpleDdgiNearMinimumUpdateQuota = 192;
                    SimpleDdgiMidMinimumUpdateQuota = 72;
                    SimpleDdgiFarMinimumUpdateQuota = 24;
                    SimpleDdgiNearMaximumUpdateQuota = 336;
                    SimpleDdgiMidMaximumUpdateQuota = 144;
                    SimpleDdgiFarMaximumUpdateQuota = 64;
                    SimpleDdgiNearMaterialTextureMaxCascade = 1;
                    SimpleDdgiMidMaterialTextureMaxCascade = 0;
                    SimpleDdgiFarMaterialTextureMaxCascade = -1;
                    SimpleDdgiNearLocalLightSamplesPerHit = 0;
                    SimpleDdgiMidLocalLightSamplesPerHit = 0;
                    SimpleDdgiFarLocalLightSamplesPerHit = 0;
                    break;

                case DdgiQualityTier.DdgiUltra:
                    SimpleDdgiNearFullRaysPerProbe = 192;
                    SimpleDdgiMidFullRaysPerProbe = 96;
                    SimpleDdgiFarFullRaysPerProbe = 48;
                    SimpleDdgiNearMaintenanceRaysPerProbe = 48;
                    SimpleDdgiMidMaintenanceRaysPerProbe = 24;
                    SimpleDdgiFarMaintenanceRaysPerProbe = 12;
                    SimpleDdgiNearMinimumUpdateQuota = 512;
                    SimpleDdgiMidMinimumUpdateQuota = 192;
                    SimpleDdgiFarMinimumUpdateQuota = 64;
                    SimpleDdgiNearMaximumUpdateQuota = 896;
                    SimpleDdgiMidMaximumUpdateQuota = 384;
                    SimpleDdgiFarMaximumUpdateQuota = 160;
                    SimpleDdgiNearMaterialTextureMaxCascade = 2;
                    SimpleDdgiMidMaterialTextureMaxCascade = 1;
                    SimpleDdgiFarMaterialTextureMaxCascade = 0;
                    SimpleDdgiNearLocalLightSamplesPerHit = 12;
                    SimpleDdgiMidLocalLightSamplesPerHit = 6;
                    SimpleDdgiFarLocalLightSamplesPerHit = 3;
                    break;

                default:
                    SimpleDdgiNearFullRaysPerProbe = 128;
                    SimpleDdgiMidFullRaysPerProbe = 64;
                    SimpleDdgiFarFullRaysPerProbe = 32;
                    SimpleDdgiNearMaintenanceRaysPerProbe = 32;
                    SimpleDdgiMidMaintenanceRaysPerProbe = 16;
                    SimpleDdgiFarMaintenanceRaysPerProbe = 8;
                    SimpleDdgiNearMinimumUpdateQuota = 512;
                    SimpleDdgiMidMinimumUpdateQuota = 96;
                    SimpleDdgiFarMinimumUpdateQuota = 24;
                    SimpleDdgiNearMaximumUpdateQuota = 1_024;
                    SimpleDdgiMidMaximumUpdateQuota = 324;
                    SimpleDdgiFarMaximumUpdateQuota = 128;
                    SimpleDdgiNearMaterialTextureMaxCascade = 1;
                    SimpleDdgiMidMaterialTextureMaxCascade = 0;
                    SimpleDdgiFarMaterialTextureMaxCascade = -1;
                    SimpleDdgiNearLocalLightSamplesPerHit = 8;
                    SimpleDdgiMidLocalLightSamplesPerHit = 4;
                    SimpleDdgiFarLocalLightSamplesPerHit = 2;
                    break;
            }
        }

        private void ApplyFarFieldPageQualityTier(DdgiQualityTier tier)
        {
            // Every DDGI tier needs a truthful coarse representation for geometry
            // intentionally excluded from its detailed streaming radius. Paged mode
            // remains bounded by the tier-specific cache budget below.
            FarFieldClipmapEnabled = true;
            FarFieldPagedEnabled = true;
            switch (tier)
            {
                case DdgiQualityTier.DdgiLow:
                    FarFieldCascadeCount = 2;
                    FarFieldPageResolution = 16;
                    FarFieldResidentPageBudget = 12;
                    FarFieldPageUpdatesPerFrame = 1;
                    FarFieldPageRequestRadius = 1;
                    FarFieldBaseVoxelSize = 2.0f;
                    FarFieldCascadeVoxelScale = 3.0f;
                    FarFieldMemoryBudgetBytes = 24UL * 1024UL * 1024UL;
                    break;

                case DdgiQualityTier.DdgiMedium:
                    FarFieldCascadeCount = 3;
                    FarFieldPageResolution = 24;
                    FarFieldResidentPageBudget = 24;
                    FarFieldPageUpdatesPerFrame = 1;
                    FarFieldPageRequestRadius = 1;
                    FarFieldBaseVoxelSize = 1.5f;
                    FarFieldCascadeVoxelScale = 3.0f;
                    FarFieldMemoryBudgetBytes = 48UL * 1024UL * 1024UL;
                    break;

                case DdgiQualityTier.DdgiUltra:
                    FarFieldCascadeCount = 4;
                    FarFieldPageResolution = 48;
                    FarFieldResidentPageBudget = 64;
                    FarFieldPageUpdatesPerFrame = 2;
                    FarFieldPageRequestRadius = 1;
                    FarFieldBaseVoxelSize = 0.75f;
                    FarFieldCascadeVoxelScale = 2.5f;
                    FarFieldMemoryBudgetBytes = 192UL * 1024UL * 1024UL;
                    break;

                default:
                    FarFieldCascadeCount = 3;
                    FarFieldPageResolution = 32;
                    FarFieldResidentPageBudget = 48;
                    FarFieldPageUpdatesPerFrame = 1;
                    FarFieldPageRequestRadius = 1;
                    FarFieldBaseVoxelSize = 1.0f;
                    FarFieldCascadeVoxelScale = 3.0f;
                    FarFieldMemoryBudgetBytes = 96UL * 1024UL * 1024UL;
                    break;
            }
        }

        private void ApplyGiAccelerationStructureQualityTier(DdgiQualityTier tier)
        {
            StreamedGiAccelerationStructuresEnabled = true;
            switch (tier)
            {
                case DdgiQualityTier.DdgiLow:
                    GiAccelerationStructureMemoryBudgetBytes = 128UL * 1024UL * 1024UL;
                    GiAccelerationStructureStaticResidentDistance = 96.0f;
                    GiAccelerationStructureMaximumStaticInstances = 1_024;
                    GiAccelerationStructureEvictionGraceFrames = 60;
                    break;

                case DdgiQualityTier.DdgiMedium:
                    GiAccelerationStructureMemoryBudgetBytes = 256UL * 1024UL * 1024UL;
                    GiAccelerationStructureStaticResidentDistance = 160.0f;
                    GiAccelerationStructureMaximumStaticInstances = 4_096;
                    GiAccelerationStructureEvictionGraceFrames = 90;
                    break;

                case DdgiQualityTier.DdgiUltra:
                    GiAccelerationStructureMemoryBudgetBytes = 1536UL * 1024UL * 1024UL;
                    GiAccelerationStructureStaticResidentDistance = 384.0f;
                    GiAccelerationStructureMaximumStaticInstances = 16_384;
                    GiAccelerationStructureEvictionGraceFrames = 240;
                    break;

                default:
                    // Detailed high-tier scenes such as Sponza exceed the old
                    // 256 MiB cap before half of their opaque meshes are admitted.
                    // Keep the working set bounded, but size it for a complete
                    // production environment rather than a partial-TLAS fallback.
                    GiAccelerationStructureMemoryBudgetBytes = 1024UL * 1024UL * 1024UL;
                    GiAccelerationStructureStaticResidentDistance = 256.0f;
                    GiAccelerationStructureMaximumStaticInstances = 8_192;
                    GiAccelerationStructureEvictionGraceFrames = 120;
                    break;
            }
        }

        private static float Clamp(float value, float min, float max)
        {
            if (!float.IsFinite(value))
                return min;
            if (value < min)
                return min;
            return value > max ? max : value;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
                return min;
            return value > max ? max : value;
        }

        private static int ClampDdgiRays(int value) =>
            Clamp(value, MinSimpleDdgiRaysPerProbe, MaxSimpleDdgiRaysPerProbe);

        private static float ClampDdgiMaxRayDistance(float value)
        {
            if (!float.IsFinite(value))
                return 0.1f;
            return Clamp(value, 0.1f, 512.0f);
        }

        private static ulong Clamp(ulong value, ulong min, ulong max)
        {
            if (value < min)
                return min;
            return value > max ? max : value;
        }
    }
}
