using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Data
{
    public enum TextureBudgetProfile : uint
    {
        Development = 0,
        HighQuality = 1,
        Cinematic = 2,
        Custom = 3
    }

    public enum ShadowDebugView : uint
    {
        None = 0,
        CascadeOverlay = 2,
        ShadowMapPreview = 3,
        ReceiverFactor = 4,
        SpotAtlasPreview = 5,
        PointCubemapFacePreview = 6,
        LocalShadowSelection = 7,
        DirectionalRayMask = 8,
        DirectionalRayHitDistance = 9,
        DirectionalRayCandidateCount = 10,
        DirectionalRaySceneResidency = 11,
        DirectionalCsmRayDifference = 12,
        DirectionalHistoryConfidence = 13,
        DirectionalHistoryRejection = 14
    }

    /// <summary>
    /// User-authored directional-shadow intent. The renderer resolves this to an
    /// effective mode only after capability, resource, and ray-scene checks pass.
    /// </summary>
    public enum DirectionalShadowMode : uint
    {
        Cascaded = 0,
        HybridContact = 1,
        RayQueryHard = 2,
        RayQuerySoft = 3
    }

    /// <summary>
    /// Optional short-history stabilization for the CSM receiver result. Auto
    /// remains inactive until a matching qualification manifest proves that
    /// stable-map residual stepping exceeds the committed threshold.
    /// </summary>
    public enum DirectionalCsmTemporalMode : uint
    {
        Disabled = 0,
        Auto = 1,
        DeveloperForce = 2
    }

    public enum DirectionalShadowFilterMode : uint
    {
        LegacyBoxPcf = 0,
        TentPcf = 1
    }

    public enum DirectionalShadowBiasMode : uint
    {
        Legacy = 0,
        WorldTexelScaled = 1
    }

    /// <summary>
    /// Controls whether every cascade uses the authored PCF radius or keeps a
    /// roughly constant world-space filter footprint as texel size grows.
    /// </summary>
    public enum DirectionalPcfRadiusMode : uint
    {
        Constant = 0,
        WorldSpaceAdaptive = 1
    }

    public sealed class ShadowSettings
    {
        public const int MaxDirectionalCascades = 4;

        private uint _directionalShadowMapSize = 2048;
        private int _directionalCascadeCount = 2;
        private int _directionalShadowPreviewCascade;
        private float _maxShadowDistance = 80f;
        private float _directionalCascadeBlendFraction = 0.12f;
        private float _directionalCascadeSplitLambda = 0.5f;
        private float _directionalCasterExtrusionDistance = 80f;
        private float _directionalContactShadowDistance = 3f;
        private DirectionalCsmTemporalMode _directionalCsmTemporalMode;
        private int _directionalSoftRecoveryRayCount = 2;
        private int _directionalSoftHistoryLength = 16;
        private int _directionalSoftSpatialPassCount = 3;
        private int _directionalTransparentSoftRayCount = 4;
        private DirectionalShadowMode _requestedDirectionalShadowMode =
            DirectionalShadowMode.Cascaded;
        private DirectionalShadowFilterMode _directionalFilterMode =
            DirectionalShadowFilterMode.LegacyBoxPcf;
        private DirectionalShadowBiasMode _directionalBiasMode =
            DirectionalShadowBiasMode.Legacy;
        private DirectionalPcfRadiusMode _directionalPcfRadiusMode =
            DirectionalPcfRadiusMode.Constant;
        private float _directionalSoftAngularDiameterScale = 1f;
        private float _normalBias = 0.03f;
        private float _slopeScaledDepthBias = 1.5f;
        private float _constantDepthBias = 0.0005f;
        private int _pcfRadius = 1;
        private int _maxShadowedSpotLights = 2;
        private uint _spotShadowAtlasSize = 4096;
        private uint _spotShadowTileSize = 512;
        private float _spotNormalBias = 0.02f;
        private float _spotConstantDepthBias = 0.0005f;
        private float _spotSlopeScaledDepthBias = 1.5f;
        private int _spotPcfRadius = 1;
        private int _maxShadowedPointLights = 1;
        private int _maxShadowedAreaLights = 2;
        private int _areaShadowSampleCount = 1;
        private uint _pointShadowMapSize = 512;
        private float _pointNormalBias = 0.03f;
        private float _pointConstantDepthBias = 0.001f;
        private float _pointSlopeScaledDepthBias = 1.5f;
        private int _pointPcfRadius = 1;

        public bool DirectionalShadowsEnabled { get; set; } = true;
        public bool SpotShadowsEnabled { get; set; } = true;
        public bool PointShadowsEnabled { get; set; } = true;
        public bool AreaShadowsEnabled { get; set; } = true;

        public DirectionalShadowMode RequestedDirectionalShadowMode
        {
            get => _requestedDirectionalShadowMode;
            set => _requestedDirectionalShadowMode = value is
                DirectionalShadowMode.Cascaded or
                DirectionalShadowMode.HybridContact or
                DirectionalShadowMode.RayQueryHard or
                DirectionalShadowMode.RayQuerySoft
                    ? value
                    : DirectionalShadowMode.Cascaded;
        }

        public DirectionalCsmTemporalMode DirectionalCsmTemporalMode
        {
            get => _directionalCsmTemporalMode;
            set => _directionalCsmTemporalMode = value is
                DirectionalCsmTemporalMode.Disabled or
                DirectionalCsmTemporalMode.Auto or
                DirectionalCsmTemporalMode.DeveloperForce
                    ? value
                    : DirectionalCsmTemporalMode.Disabled;
        }

        /// <summary>
        /// Runtime-only admission supplied by a verified qualification
        /// manifest. Saving settings can never manufacture this authority.
        /// </summary>
        [JsonIgnore]
        public bool DirectionalCsmTemporalQualificationApproved { get; internal set; }

        [JsonIgnore]
        public bool EffectiveDirectionalCsmTemporalEnabled =>
            DirectionalCsmTemporalMode == DirectionalCsmTemporalMode.DeveloperForce ||
            DirectionalCsmTemporalMode == DirectionalCsmTemporalMode.Auto &&
            DirectionalCsmTemporalQualificationApproved;

        public DirectionalShadowFilterMode DirectionalFilterMode
        {
            get => _directionalFilterMode;
            set => _directionalFilterMode = value is
                DirectionalShadowFilterMode.LegacyBoxPcf or
                DirectionalShadowFilterMode.TentPcf
                    ? value
                    : DirectionalShadowFilterMode.LegacyBoxPcf;
        }

        public DirectionalShadowBiasMode DirectionalBiasMode
        {
            get => _directionalBiasMode;
            set => _directionalBiasMode = value is
                DirectionalShadowBiasMode.Legacy or
                DirectionalShadowBiasMode.WorldTexelScaled
                    ? value
                    : DirectionalShadowBiasMode.Legacy;
        }

        public DirectionalPcfRadiusMode DirectionalPcfRadiusMode
        {
            get => _directionalPcfRadiusMode;
            set => _directionalPcfRadiusMode = value is
                DirectionalPcfRadiusMode.Constant or
                DirectionalPcfRadiusMode.WorldSpaceAdaptive
                    ? value
                    : DirectionalPcfRadiusMode.Constant;
        }

        public uint DirectionalShadowMapSize
        {
            get => _directionalShadowMapSize;
            set => _directionalShadowMapSize = ClampPowerOfTwo(value, 512, 4096);
        }

        public int DirectionalCascadeCount
        {
            get => _directionalCascadeCount;
            set => _directionalCascadeCount = value < 1 ? 1 : value > MaxDirectionalCascades ? MaxDirectionalCascades : value;
        }

        /// <summary>
        /// Directional cascade displayed by <see cref="ShadowDebugView.ShadowMapPreview"/>.
        /// The renderer also clamps this to the currently active cascade count.
        /// </summary>
        public int DirectionalShadowPreviewCascade
        {
            get => _directionalShadowPreviewCascade;
            set => _directionalShadowPreviewCascade = Math.Clamp(value, 0, MaxDirectionalCascades - 1);
        }

        public float MaxShadowDistance
        {
            get => _maxShadowDistance;
            set => _maxShadowDistance = Clamp(value, 1f, 1000f);
        }

        /// <summary>
        /// Fraction of the smaller neighbouring cascade span used to overlap and cross-fade
        /// directional-shadow cascades.  The overlap keeps both receiver projections valid at
        /// a split, rather than treating the boundary as a hard ownership change.
        /// </summary>
        public float DirectionalCascadeBlendFraction
        {
            get => _directionalCascadeBlendFraction;
            set => _directionalCascadeBlendFraction = Clamp(value, 0.02f, 0.30f);
        }

        /// <summary>
        /// Practical split-scheme blend between uniform (0) and logarithmic (1)
        /// camera-frustum partitions.
        /// </summary>
        public float DirectionalCascadeSplitLambda
        {
            get => _directionalCascadeSplitLambda;
            set => _directionalCascadeSplitLambda = Clamp(value, 0f, 1f);
        }

        /// <summary>
        /// Conservative distance searched from a cascade receiver volume toward
        /// the directional light when exact caster bounds are unavailable.
        /// </summary>
        public float DirectionalCasterExtrusionDistance
        {
            get => _directionalCasterExtrusionDistance;
            set => _directionalCasterExtrusionDistance = Clamp(value, 1f, 5000f);
        }

        /// <summary>Maximum ray length used by bounded hybrid contact shadows.</summary>
        public float DirectionalContactShadowDistance
        {
            get => MathF.Min(_directionalContactShadowDistance, MaxShadowDistance);
            set => _directionalContactShadowDistance = Clamp(value, 0.1f, 100f);
        }

        /// <summary>
        /// Artistic multiplier for the environment sun diameter used by
        /// finite-sun ray-query shadows. Zero intentionally resolves the soft
        /// mode to deterministic hard rays.
        /// </summary>
        public float DirectionalSoftAngularDiameterScale
        {
            get => _directionalSoftAngularDiameterScale;
            set => _directionalSoftAngularDiameterScale = float.IsFinite(value)
                ? Math.Clamp(value, 0f, 8f)
                : 1f;
        }

        /// <summary>Additional finite-sun rays used after history rejection.</summary>
        public int DirectionalSoftRecoveryRayCount
        {
            get => _directionalSoftRecoveryRayCount;
            set => _directionalSoftRecoveryRayCount = Math.Clamp(value, 1, 4);
        }

        /// <summary>Maximum accepted finite-sun history age in frames.</summary>
        public int DirectionalSoftHistoryLength
        {
            get => _directionalSoftHistoryLength;
            set => _directionalSoftHistoryLength = Math.Clamp(value, 1, 32);
        }

        /// <summary>Edge-aware A-trous passes for finite-sun visibility.</summary>
        public int DirectionalSoftSpatialPassCount
        {
            get => _directionalSoftSpatialPassCount;
            set => _directionalSoftSpatialPassCount = Math.Clamp(value, 0, 3);
        }

        /// <summary>
        /// Direct finite-sun samples for layered transparent receivers, which
        /// deliberately do not consume opaque screen history.
        /// </summary>
        public int DirectionalTransparentSoftRayCount
        {
            get => _directionalTransparentSoftRayCount;
            set => _directionalTransparentSoftRayCount = Math.Clamp(value, 1, 4);
        }

        public float NormalBias
        {
            get => _normalBias;
            set => _normalBias = Clamp(value, 0f, 1f);
        }

        public float SlopeScaledDepthBias
        {
            get => _slopeScaledDepthBias;
            set => _slopeScaledDepthBias = Clamp(value, 0f, 16f);
        }

        public float ConstantDepthBias
        {
            get => _constantDepthBias;
            set => _constantDepthBias = Clamp(value, 0f, 0.1f);
        }

        public int PcfRadius
        {
            get => _pcfRadius;
            set => _pcfRadius = value < 0 ? 0 : value > 3 ? 3 : value;
        }

        public int MaxShadowedSpotLights
        {
            get => Math.Min(_maxShadowedSpotLights, SpotShadowAtlasCapacity);
            set => _maxShadowedSpotLights = value < 0 ? 0 : value > 32 ? 32 : value;
        }

        public uint SpotShadowAtlasSize
        {
            get => _spotShadowAtlasSize;
            set => _spotShadowAtlasSize = ClampPowerOfTwo(value, 1024, 8192);
        }

        public uint SpotShadowTileSize
        {
            get => Math.Min(_spotShadowTileSize, _spotShadowAtlasSize);
            set => _spotShadowTileSize = ClampPowerOfTwo(value, 128, 2048);
        }

        public float SpotNormalBias
        {
            get => _spotNormalBias;
            set => _spotNormalBias = Clamp(value, 0f, 1f);
        }

        public float SpotConstantDepthBias
        {
            get => _spotConstantDepthBias;
            set => _spotConstantDepthBias = Clamp(value, 0f, 0.1f);
        }

        public float SpotSlopeScaledDepthBias
        {
            get => _spotSlopeScaledDepthBias;
            set => _spotSlopeScaledDepthBias = Clamp(value, 0f, 16f);
        }

        public int SpotPcfRadius
        {
            get => _spotPcfRadius;
            set => _spotPcfRadius = value < 0 ? 0 : value > 3 ? 3 : value;
        }

        public int MaxShadowedPointLights
        {
            get => _maxShadowedPointLights;
            set => _maxShadowedPointLights = value < 0 ? 0 : value > 4 ? 4 : value;
        }

        /// <summary>Maximum area emitters receiving full-resolution ray-query masks.</summary>
        public int MaxShadowedAreaLights
        {
            get => _maxShadowedAreaLights;
            set => _maxShadowedAreaLights = Math.Clamp(value, 0, 4);
        }

        /// <summary>Visibility samples per selected area emitter and opaque pixel.</summary>
        public int AreaShadowSampleCount
        {
            get => _areaShadowSampleCount;
            set => _areaShadowSampleCount = Math.Clamp(value, 1, 4);
        }

        public uint PointShadowMapSize
        {
            get => _pointShadowMapSize;
            set => _pointShadowMapSize = ClampPowerOfTwo(value, 128, 2048);
        }

        public float PointNormalBias
        {
            get => _pointNormalBias;
            set => _pointNormalBias = Clamp(value, 0f, 1f);
        }

        public float PointConstantDepthBias
        {
            get => _pointConstantDepthBias;
            set => _pointConstantDepthBias = Clamp(value, 0f, 0.1f);
        }

        public float PointSlopeScaledDepthBias
        {
            get => _pointSlopeScaledDepthBias;
            set => _pointSlopeScaledDepthBias = Clamp(value, 0f, 16f);
        }

        public int PointPcfRadius
        {
            get => _pointPcfRadius;
            set => _pointPcfRadius = value < 0 ? 0 : value > 3 ? 3 : value;
        }

        public int SpotShadowAtlasCapacity
        {
            get
            {
                uint tileSize = SpotShadowTileSize;
                uint tilesPerSide = tileSize == 0 ? 0 : SpotShadowAtlasSize / tileSize;
                uint capacity = tilesPerSide * tilesPerSide;
                return capacity > int.MaxValue ? int.MaxValue : (int)capacity;
            }
        }

        public ShadowDebugView DebugView { get; set; } = ShadowDebugView.None;

        /// <summary>
        /// Diagnostic override that refreshes every active directional static-shadow cascade
        /// every frame. Leave disabled outside cache-lifecycle investigations.
        /// </summary>
        public bool ForceStaticCascadeCacheRefresh { get; set; }

        private static uint ClampPowerOfTwo(uint value, uint min, uint max)
        {
            if (value < min)
                return min;
            if (value > max)
                return max;

            uint rounded = 1;
            while (rounded < value)
                rounded <<= 1;
            return rounded;
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min)
                return min;
            return value > max ? max : value;
        }
    }

    public enum BloomDebugView : uint
    {
        None = 0,
        ExtractMask = 1,
        DownsampleMip = 2,
        UpsampleResult = 3,
        BloomOnly = 4
    }

    public enum ToneMapper : uint
    {
        None = 0,
        Reinhard = 1,
        AcesFitted = 2
    }

    public sealed class AutoExposureSettings
    {
        private float _targetLuminance = 0.125f;
        private float _minExposure = 0.25f;
        private float _maxExposure = 4.0f;
        private float _lowPercentile = 70.0f;
        private float _highPercentile = 95.0f;
        private float _darkToLightAdaptationSpeed = 3.0f;
        private float _lightToDarkAdaptationSpeed = 1.0f;
        private float _minLogLuminance = -10.0f;
        private float _maxLogLuminance = 4.0f;
        private int _samplingStride = 4;

        public bool Enabled { get; set; }

        public float TargetLuminance
        {
            get => _targetLuminance;
            set => _targetLuminance = Clamp(value, 0.01f, 1.0f);
        }

        public float MinExposure
        {
            get => _minExposure;
            set
            {
                _minExposure = Clamp(value, 0.001f, 1024.0f);
                if (_maxExposure < _minExposure)
                    _maxExposure = _minExposure;
            }
        }

        public float MaxExposure
        {
            get => _maxExposure;
            set => _maxExposure = Clamp(value, _minExposure, 1024.0f);
        }

        public float LowPercentile
        {
            get => _lowPercentile;
            set
            {
                _lowPercentile = Clamp(value, 0.0f, 99.99f);
                if (_highPercentile <= _lowPercentile)
                    _highPercentile = Math.Min(100.0f, _lowPercentile + 0.01f);
            }
        }

        public float HighPercentile
        {
            get => _highPercentile;
            set => _highPercentile = Clamp(value, _lowPercentile + 0.01f, 100.0f);
        }

        public float DarkToLightAdaptationSpeed
        {
            get => _darkToLightAdaptationSpeed;
            set => _darkToLightAdaptationSpeed = Clamp(value, 0.0f, 30.0f);
        }

        public float LightToDarkAdaptationSpeed
        {
            get => _lightToDarkAdaptationSpeed;
            set => _lightToDarkAdaptationSpeed = Clamp(value, 0.0f, 30.0f);
        }

        public float MinLogLuminance
        {
            get => _minLogLuminance;
            set
            {
                _minLogLuminance = Clamp(value, -24.0f, 16.0f);
                if (_maxLogLuminance <= _minLogLuminance + 0.01f)
                    _maxLogLuminance = _minLogLuminance + 0.01f;
            }
        }

        public float MaxLogLuminance
        {
            get => _maxLogLuminance;
            set => _maxLogLuminance = Clamp(value, _minLogLuminance + 0.01f, 24.0f);
        }

        public int SamplingStride
        {
            get => _samplingStride;
            set => _samplingStride = value <= 1 ? 1 : value <= 2 ? 2 : value <= 4 ? 4 : 8;
        }

        public float LogLuminanceRange => _maxLogLuminance - _minLogLuminance;

        private static float Clamp(float value, float min, float max)
        {
            if (value < min)
                return min;
            return value > max ? max : value;
        }
    }

    public enum EnvironmentDebugView : uint
    {
        None = 0,
        SkyboxOnly = 1,
        IrradianceCubemap = 2,
        PrefilteredEnvironmentMip = 3,
        BrdfLut = 4,
        DiffuseIblOnly = 5,
        SpecularIblOnly = 6,
        AmbientOcclusion = 7
    }

    public enum AmbientOcclusionMode : uint
    {
        Disabled = 0,
        Ssao = 1,
        Gtao = 2
    }

    public enum AmbientOcclusionDebugView : uint
    {
        None = 0,
        RawAo = 1,
        BlurredAo = 2,
        FinalAo = 3,
        ReconstructedNormal = 4,
        LinearDepth = 5,
        RawGtaoVisibility = 6,
        GtaoHorizonContribution = 7,
        RawGtaoBentNormal = 8,
        FilteredGtaoBentNormal = 9,
        GtaoTemporalConfidence = 10,
        GtaoHistoryRejection = 11,
        FinalGtao = 12
    }

    public enum GtaoQualityPreset : uint
    {
        Low = 0,
        Balanced = 1,
        High = 2
    }

    public enum AmbientOcclusionBentNormalMode : uint
    {
        Off = 0,
        EnvironmentOnly = 1,
        EnvironmentAndDdgi = 2
    }

    public enum AmbientOcclusionForwardSamplingMode : uint
    {
        Disabled = 0,
        Direct = 1,
        DepthAwareUpsample = 2
    }

    public enum GlobalIlluminationMode : uint
    {
        Disabled = 0,
        Ddgi = 1
    }

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

    public enum AntiAliasingMode : uint
    {
        None = 0,
        Fxaa = 1,
        SmaaLow = 2,
        SmaaMedium = 3,
        SmaaHigh = 4,
        Taa = 5
    }

    public enum AntiAliasingDebugView : uint
    {
        None = 0,
        InputColor = 1,
        FxaaLuma = 2,
        SmaaEdges = 3,
        SmaaBlendWeights = 4,
        MotionVectors = 5,
        JitterPattern = 6,
        TaaHistory = 7
    }

    public enum FogMode : uint
    {
        Disabled = 0,
        Distance = 1,
        Height = 2,
        DistanceAndHeight = 3
    }

    public enum FogTechnique : uint
    {
        Auto = 0,
        Analytic = 1,
        Froxel = 2
    }

    public enum FogColorMode : uint
    {
        ConstantColor = 0,
        SkyColor = 1,
        SkyAndConstantBlend = 2
    }

    public enum FogDebugView : uint
    {
        None = 0,
        FogFactor = 1,
        Transmittance = 2,
        DistanceFog = 3,
        HeightFog = 4,
        Inscattering = 5,
        LinearDepth = 6,
        WorldHeight = 7,
        FoggedScene = 8,
        Density = 9,
        Extinction = 10,
        DirectRadiance = 11,
        IndirectRadiance = 12,
        HistoryConfidence = 13,
        FinalTransmittance = 14,
        SelfShadowing = 15
    }

    internal static class FogDebugViewPolicy
    {
        public static bool IsDisplayReferred(FogDebugView debugView)
        {
            // FoggedScene remains HDR beauty output and must follow the normal
            // exposure/tone-map path. All other non-None diagnostics are
            // normalized by their fog producer for direct display.
            return debugView is not FogDebugView.None and
                not FogDebugView.FoggedScene;
        }

    }

    /// <summary>
    /// Selects how a three-dimensional froxel field is reduced for a
    /// two-dimensional diagnostic view.
    /// </summary>
    public enum FogDebugProjection : uint
    {
        MaxAlongRay = 0,
        Surface = 1,
        Slice = 2
    }

    public enum EnvironmentSourceKind : uint
    {
        ProceduralSky = 0,
        HdrEquirectangular = 1,
        Cubemap = 2
    }

    public enum ProceduralSkySunDriver : uint
    {
        SceneDirectionalLight = 0,
        AstronomicalTime = 1,
        DirectSunDirection = 2
    }

    public enum EnvironmentTexturePrecision : uint
    {
        Float16 = 0,
        Float32 = 1
    }

    public enum ReflectionMode : uint
    {
        Disabled = 0,
        GlobalEnvironmentOnly = 1,
        StaticProbes = 2,
        StaticProbesAndSsr = 3,
        StaticProbesAndPlanar = 4,
        /// <summary>
        /// Screen-space reflections first, bounded ray-query recovery second,
        /// directional DDGI as the stable off-screen base, then local probes
        /// and the global environment as compatibility fallbacks.
        /// </summary>
        HybridRayQuery = 5
    }

    public enum ReflectionDebugView : uint
    {
        None = 0,
        ProbeInfluence = 1,
        ProbeIndex = 2,
        ProbeBlendWeights = 3,
        ProbeCubemapFace = 4,
        ProbePrefilterMip = 5,
        BoxProjectionDirection = 6,
        SsrMask = 7,
        PlanarReflection = 8,
        LocalReflectionOnly = 9,
        GlobalFallbackOnly = 10,
        /// <summary>
        /// Evaluated directional-DDGI incident radiance in the receiver's
        /// reflected direction, before the split-sum BRDF is applied.
        /// </summary>
        DdgiDirectionalRadianceLobe = 11,
        /// <summary>
        /// Normalized indirect-specular ownership: red is local geometric
        /// reflection, green is directional DDGI, and blue is environment.
        /// </summary>
        SourceOwnership = 12,
        /// <summary>Final reflection confidence after temporal validation.</summary>
        Confidence = 13,
        /// <summary>Final source: SSR cyan, ray query magenta, probe yellow, environment blue.</summary>
        SourceSelection = 14,
        /// <summary>
        /// Adaptive update cost in red, transmission in green, and broad
        /// anisotropy in blue.
        /// </summary>
        DetailBudget = 15,
        /// <summary>
        /// Receiver material inputs: roughness in red, maximum F0 in green,
        /// and indirect-specular visibility in blue.
        /// </summary>
        ReceiverMaterial = 16,
        /// <summary>
        /// Authored physical roughness in red, conservative reflection-work
        /// scheduling roughness in green, and their absolute delta in blue.
        /// </summary>
        RoughnessInputs = 17
    }

    public enum TransparencyMode : uint
    {
        SortedAlphaBlend = 0,
        WeightedBlendedOit = 1
    }

    public enum TransparencyDebugView : uint
    {
        None = 0,
        AlphaMode = 1,
        AlphaValue = 2,
        AlphaCutoff = 3,
        TransparentSortOrder = 4,
        Overdraw = 5,
        WeightedOitAccumulation = 6,
        WeightedOitRevealage = 7
    }

    public enum ThickTransmissionMode : uint
    {
        Off = 0,
        Approximation = 1,
        RayQuery = 2
    }

    public enum DispersionMode : uint
    {
        Off = 0,
        RgbTriplet = 1
    }

    public enum DecalDebugView : uint
    {
        None = 0,
        GeometryDecalMask = 1,
        DecalLayer = 2,
        DecalDepthBias = 3,
        ProjectedDecalVolume = 4,
        ProjectedDecalAtlas = 5
    }

    public enum AnimationSkinningMode : uint
    {
        Disabled = 0,
        CpuDebug = 1,
        GpuCompute = 2
    }

    public enum AnimationDebugView : uint
    {
        None = 0,
        SkinnedObjects = 64,
        JointWeights = 65,
        JointIndex = 66,
        SkinningError = 67,
        Skeleton = 68,
        AnimatedBounds = 69,
        ClipTime = 70
    }

    public enum ParticleSimulationMode : uint
    {
        Cpu = 0,
        Gpu = 1
    }

    public enum ParticleDebugView : uint
    {
        None = 0,
        Bounds = 1,
        Overdraw = 2,
        SoftParticleFade = 3,
        FlipbookFrame = 4,
        SortOrder = 5,
        Lifetime = 6,
        Velocity = 7,
        EmitterId = 8,
        BatchId = 9,
        BudgetHeatmap = 10
    }

    public enum MaterialDebugView : uint
    {
        None = 0,
        FeatureFlags = 32,
        BaseColor = 33,
        Metallic = 34,
        Roughness = 35,
        NormalStrength = 36,
        WorldNormal = 37,
        EmissiveIntensity = 38,
        ClearcoatFactor = 39,
        ClearcoatRoughness = 40,
        SheenColor = 41,
        SheenRoughness = 42,
        AnisotropyStrength = 43,
        AnisotropyDirection = 44,
        Transmission = 45,
        Ior = 46,
        VolumeThickness = 47,
        AttenuationColor = 48,
        SubsurfaceStrength = 49,
        SpecularFactor = 50,
        SpecularColor = 51,
        IridescenceFactor = 52,
        IridescenceThickness = 53,
        Dispersion = 54,
        MaterialOcclusion = 55,
        CanonicalDiffuseReflectance = 56,
        CompiledEmission = 57,
        GeometricNormal = 58,
        Opacity = 59,
        Sidedness = 60,
        ShadingModel = 61,
        TransportProfile = 62,
        MaterialRevisions = 63,
        /// <summary>
        /// Capture-only linear direct-diffuse signal. This value intentionally
        /// sits outside the interactive material-inspection range.
        /// </summary>
        CaptureLinearDirectDiffuse = 71,
        /// <summary>
        /// Capture-only direct specular, evaluated as aggregate direct lighting
        /// minus the exact diffuse term accumulated by the same shader.
        /// </summary>
        CaptureLinearDirectSpecular = 72
    }

    public static class MaterialDebugViewPolicy
    {
        public static bool IsLinearDirectCapture(MaterialDebugView view) =>
            view is MaterialDebugView.CaptureLinearDirectDiffuse or
                MaterialDebugView.CaptureLinearDirectSpecular;
    }

    public enum FoliageDebugView : uint
    {
        None = 0,
        Clusters = 1,
        LodBands = 2,
        DensityFade = 3,
        WindStrength = 4,
        HiZRejectedClusters = 5,
        ShadowCasting = 6,
        AlphaCutoff = 7
    }

    public enum RenderQualityPreset : uint
    {
        Low = 0,
        Medium = 1,
        High = 2,
        Ultra = 3,
        DdgiHigh = 4
    }

    /// <summary>
    /// Controls the conservative, attachment-based Vulkan fragment shading-rate
    /// path. Auto never forces coarse shading: the per-frame safety policy and
    /// GPU classifier can retain 1x1 shading for every tile.
    /// </summary>
    public enum VariableRateShadingMode : uint
    {
        Off = 0,
        Auto = 1
    }

    public sealed class RasterSettings
    {
        private VariableRateShadingMode _variableRateShadingMode;

        public VariableRateShadingMode VariableRateShadingMode
        {
            get => _variableRateShadingMode;
            set => _variableRateShadingMode = Enum.IsDefined(value)
                ? value
                : VariableRateShadingMode.Off;
        }
    }

    /// <summary>
    /// Selects the policy used by GPU scene submission to choose a cooked
    /// meshlet LOD. Screen-space error is resolution and field-of-view aware;
    /// LegacyDistance preserves the pre-1.5 cooked-content behavior.
    /// </summary>
    public enum GpuLodSelectionMode : uint
    {
        LegacyDistance = 0,
        ScreenSpaceError = 1
    }

    public enum SpecularAntialiasingMode : uint
    {
        Off = 0,
        GeometricVariance = 1
    }

    public enum RenderFeatureIsolationMode : uint
    {
        FullFrame = 0,
        Geometry = 1,
        Shadows = 2,
        PostProcessing = 3,
        Reflections = 4,
        Animation = 5,
        Particles = 6
    }

    public sealed class MaterialSettings
    {
        private SpecularAntialiasingMode _specularAntialiasingMode =
            SpecularAntialiasingMode.GeometricVariance;

        public MaterialDebugView DebugView { get; set; } = MaterialDebugView.None;

        public SpecularAntialiasingMode SpecularAntialiasingMode
        {
            get => _specularAntialiasingMode;
            set => _specularAntialiasingMode = Enum.IsDefined(value)
                ? value
                : SpecularAntialiasingMode.GeometricVariance;
        }
    }

    public sealed class FoliageSettings
    {
        private float _grassShadowDistance = 25f;
        private float _grassShadowDensityScale = 0.5f;
        private float _maxDrawDistance = 250f;
        private float _densityScale = 1f;
        private int _maxVisibleClusters = 262144;
        private int _maxVisibleMeshletDraws = 524288;
        private int _maxLocalShadowedSpotLights = 1;
        private int _maxLocalShadowedPointLights = 1;
        private int _maxLocalShadowClusters = 4096;
        private int _maxLocalShadowMeshletDraws = 8192;

        public bool Enabled { get; set; } = true;
        public bool GpuDrivenEnabled { get; set; } = true;
        public bool HiZCullingEnabled { get; set; } = true;
        public bool CastShadows { get; set; } = true;
        public bool IndirectMeshletDispatchEnabled { get; set; } = true;
        public bool FarImpostorsEnabled { get; set; } = true;
        public bool MotionVectorsEnabled { get; set; } = true;
        public bool LocalShadowsEnabled { get; set; } = true;

        public float GrassShadowDistance
        {
            get => _grassShadowDistance;
            set => _grassShadowDistance = Clamp(value, 0.0f, 1000.0f);
        }

        public float GrassShadowDensityScale
        {
            get => _grassShadowDensityScale;
            set => _grassShadowDensityScale = Clamp(value, 0.0f, 1.0f);
        }

        public float MaxDrawDistance
        {
            get => _maxDrawDistance;
            set => _maxDrawDistance = Clamp(value, 0.0f, 10000.0f);
        }

        public float DensityScale
        {
            get => _densityScale;
            set => _densityScale = Clamp(value, 0.0f, 8.0f);
        }

        public int MaxVisibleClusters
        {
            get => _maxVisibleClusters;
            set => _maxVisibleClusters = Clamp(value, 0, 4_194_304);
        }

        public int MaxVisibleMeshletDraws
        {
            get => _maxVisibleMeshletDraws;
            set => _maxVisibleMeshletDraws = Clamp(value, 0, 8_388_608);
        }

        public int MaxLocalShadowedSpotLights
        {
            get => _maxLocalShadowedSpotLights;
            set => _maxLocalShadowedSpotLights = Clamp(value, 0, 8);
        }

        public int MaxLocalShadowedPointLights
        {
            get => _maxLocalShadowedPointLights;
            set => _maxLocalShadowedPointLights = Clamp(value, 0, 4);
        }

        public int MaxLocalShadowClusters
        {
            get => _maxLocalShadowClusters;
            set => _maxLocalShadowClusters = Clamp(value, 0, 262144);
        }

        public int MaxLocalShadowMeshletDraws
        {
            get => _maxLocalShadowMeshletDraws;
            set => _maxLocalShadowMeshletDraws = Clamp(value, 0, 524288);
        }

        public FoliageDebugView DebugView { get; set; } = FoliageDebugView.None;

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
    }

    public sealed class SceneSubmissionSettings
    {
        public const float DefaultGpuLod1DistanceRatio = 4.0f;
        public const float DefaultGpuLod2DistanceRatio = 10.0f;
        public const float DefaultGpuLodTargetPixelError = 1.0f;
        public const float GpuLodHysteresisFraction = 0.15f;
        public const int DefaultGpuShadowLodBias = 1;

        private float _gpuLod1DistanceRatio = DefaultGpuLod1DistanceRatio;
        private float _gpuLod2DistanceRatio = DefaultGpuLod2DistanceRatio;
        private GpuLodSelectionMode _gpuLodSelectionMode =
            GpuLodSelectionMode.ScreenSpaceError;
        private float _gpuLodTargetPixelError =
            DefaultGpuLodTargetPixelError;
        private int _gpuShadowLodBias = DefaultGpuShadowLodBias;

        public bool GpuCompactionEnabled { get; set; } = true;
        public bool IndirectMeshletDispatchEnabled { get; set; } = true;
        public bool GpuLodSelectionEnabled { get; set; } = true;

        public GpuLodSelectionMode GpuLodSelectionMode
        {
            get => _gpuLodSelectionMode;
            set => _gpuLodSelectionMode = Enum.IsDefined(value)
                ? value
                : GpuLodSelectionMode.ScreenSpaceError;
        }

        /// <summary>
        /// Maximum projected geometric deviation admitted for a cooked LOD.
        /// Lower values retain finer geometry for longer.
        /// </summary>
        public float GpuLodTargetPixelError
        {
            get => _gpuLodTargetPixelError;
            set => _gpuLodTargetPixelError =
                ClampGpuLodTargetPixelError(value);
        }

        /// <summary>
        /// Distance-to-bounding-radius ratio at which GPU scene submission switches from LOD0 to LOD1.
        /// </summary>
        public float GpuLod1DistanceRatio
        {
            get => _gpuLod1DistanceRatio;
            set
            {
                _gpuLod1DistanceRatio = ClampGpuLod1DistanceRatio(value);
                if (_gpuLod2DistanceRatio < _gpuLod1DistanceRatio)
                    _gpuLod2DistanceRatio = _gpuLod1DistanceRatio;
            }
        }

        /// <summary>
        /// Distance-to-bounding-radius ratio at which GPU scene submission switches from LOD1 to LOD2.
        /// This is always at least <see cref="GpuLod1DistanceRatio"/>.
        /// </summary>
        public float GpuLod2DistanceRatio
        {
            get => _gpuLod2DistanceRatio;
            set => _gpuLod2DistanceRatio = ClampGpuLod2DistanceRatio(value, _gpuLod1DistanceRatio);
        }

        public bool GpuShadowCompactionEnabled { get; set; } = true;

        /// <summary>
        /// Additional requested LOD levels for GPU-compacted directional-shadow draws.
        /// The current command stream is indexed by LOD0 meshlets, so directional shadows
        /// conservatively retain LOD0 whenever a lower LOD lacks a topology-safe mapping.
        /// The requested lower-LOD count remains visible in directional-shadow diagnostics.
        /// </summary>
        public int GpuShadowLodBias
        {
            get => _gpuShadowLodBias;
            set => _gpuShadowLodBias = Math.Clamp(value, 0, 2);
        }

        public bool ValidationCompareCpuGpuLists { get; set; }

        internal static float ClampGpuLod1DistanceRatio(float value) =>
            ClampFinite(value, minimum: 1.0f, maximum: 64.0f, fallback: DefaultGpuLod1DistanceRatio);

        internal static float ClampGpuLod2DistanceRatio(float value, float gpuLod1DistanceRatio) =>
            Math.Max(
                ClampGpuLod1DistanceRatio(gpuLod1DistanceRatio),
                ClampFinite(value, minimum: 1.0f, maximum: 128.0f, fallback: DefaultGpuLod2DistanceRatio));

        internal static float ClampGpuLodTargetPixelError(float value) =>
            ClampFinite(
                value,
                minimum: 0.125f,
                maximum: 8.0f,
                fallback: DefaultGpuLodTargetPixelError);

        private static float ClampFinite(float value, float minimum, float maximum, float fallback) =>
            float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
    }

    public sealed class DynamicResolutionSettings
    {
        private float _minimumScale = 0.7f;
        private float _maximumScale = 1.0f;
        private float _targetFrameMilliseconds = 16.67f;
        private float _adjustmentRate = 0.05f;

        public bool Enabled { get; set; } = true;

        public float MinimumScale
        {
            get => _minimumScale;
            set
            {
                _minimumScale = ClampScale(value);
                if (_maximumScale < _minimumScale)
                    _maximumScale = _minimumScale;
            }
        }

        public float MaximumScale
        {
            get => _maximumScale;
            set => _maximumScale = Math.Max(_minimumScale, ClampScale(value));
        }

        public float TargetFrameMilliseconds
        {
            get => _targetFrameMilliseconds;
            set => _targetFrameMilliseconds = Clamp(value, 1.0f, 1000.0f);
        }

        public float AdjustmentRate
        {
            get => _adjustmentRate;
            set => _adjustmentRate = Clamp(value, 0.001f, 1.0f);
        }

        internal float ClampResolvedScale(float requestedScale)
        {
            float clamped = ClampScale(requestedScale);
            return Enabled ? Clamp(clamped, _minimumScale, _maximumScale) : clamped;
        }

        private static float ClampScale(float value)
        {
            return Clamp(value, 0.5f, 1.0f);
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min)
                return min;
            return value > max ? max : value;
        }
    }

    public sealed class AnimationSettings
    {
        private int _maxJointsPerSkeleton = 256;
        private int _maxAnimatedInstances = 1024;
        private float _boundsPadding = 0.25f;

        public bool Enabled { get; set; } = true;
        public AnimationSkinningMode SkinningMode { get; set; } = AnimationSkinningMode.GpuCompute;
        public AnimationDebugView DebugView { get; set; } = AnimationDebugView.None;

        public int MaxJointsPerSkeleton
        {
            get => _maxJointsPerSkeleton;
            set => _maxJointsPerSkeleton = value < 1 ? 1 : value > 1024 ? 1024 : value;
        }

        public int MaxAnimatedInstances
        {
            get => _maxAnimatedInstances;
            set => _maxAnimatedInstances = value < 0 ? 0 : value;
        }

        public bool UpdateWhenOffscreen { get; set; } = true;
        public bool UseConservativeBounds { get; set; } = true;

        public float BoundsPadding
        {
            get => _boundsPadding;
            set => _boundsPadding = Clamp(value, 0.0f, 10.0f);
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min)
                return min;
            return value > max ? max : value;
        }
    }

    public sealed class ParticleSettings
    {
        private int _maxParticles = 65536;
        private int _maxEmitters = 1024;
        private int _maxBatches = 4096;
        private int _maxTrails = 4096;
        private int _maxTrailSegments = 65536;
        private float _softParticleDistance = 0.35f;
        private float _globalSpawnRateScale = 1.0f;
        private float _globalVelocityScale = 1.0f;
        private float _globalEmissiveScale = 1.0f;
        private float _distanceCullMultiplier = 1.0f;
        private float _fixedSimulationDeltaSeconds;

        public bool Enabled { get; set; } = true;
        public ParticleSimulationMode SimulationMode { get; set; } = ParticleSimulationMode.Cpu;
        public ParticleDebugView DebugView { get; set; } = ParticleDebugView.None;

        /// <summary>
        /// Optional fixed simulation timestep used by deterministic captures and benchmarks.
        /// A value of zero keeps the normal wall-clock timestep.
        /// </summary>
        public float FixedSimulationDeltaSeconds
        {
            get => _fixedSimulationDeltaSeconds;
            set => _fixedSimulationDeltaSeconds = float.IsFinite(value)
                ? Clamp(value, 0.0f, 1.0f / 15.0f)
                : 0.0f;
        }

        public int MaxParticles
        {
            get => _maxParticles;
            set => _maxParticles = Clamp(value, 0, 1_000_000);
        }

        public int MaxEmitters
        {
            get => _maxEmitters;
            set => _maxEmitters = Clamp(value, 0, 65535);
        }

        public int MaxBatches
        {
            get => _maxBatches;
            set => _maxBatches = Clamp(value, 0, 65535);
        }

        public int MaxTrails
        {
            get => _maxTrails;
            set => _maxTrails = Clamp(value, 0, 65535);
        }

        public int MaxTrailSegments
        {
            get => _maxTrailSegments;
            set => _maxTrailSegments = Clamp(value, 0, 1_000_000);
        }

        public bool SoftParticlesEnabled { get; set; } = true;

        public float SoftParticleDistance
        {
            get => _softParticleDistance;
            set => _softParticleDistance = Clamp(value, 0.0f, 10.0f);
        }

        public bool DepthTestEnabled { get; set; } = true;
        public bool ReceiveFog { get; set; } = true;
        public bool UsePremultipliedAlphaByDefault { get; set; } = true;

        public float GlobalSpawnRateScale
        {
            get => _globalSpawnRateScale;
            set => _globalSpawnRateScale = Clamp(value, 0.0f, 10.0f);
        }

        public float GlobalVelocityScale
        {
            get => _globalVelocityScale;
            set => _globalVelocityScale = Clamp(value, 0.0f, 10.0f);
        }

        public float GlobalEmissiveScale
        {
            get => _globalEmissiveScale;
            set => _globalEmissiveScale = Clamp(value, 0.0f, 64.0f);
        }

        public float DistanceCullMultiplier
        {
            get => _distanceCullMultiplier;
            set => _distanceCullMultiplier = Clamp(value, 0.0f, 100.0f);
        }

        public ulong MaxUploadBytesPerFrame { get; set; } = 8 * 1024 * 1024;

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
                return min;
            return value > max ? max : value;
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min)
                return min;
            return value > max ? max : value;
        }
    }

    public sealed class TransparencySettings
    {
        public const int MaximumSceneReflectionRayTaskBudget = 4_194_304;
        public const int MaximumSceneReflectionSsrSampleBudget = 16_777_216;

        private int _maxTransparentMeshlets = 262144;
        private float _alphaDiscardThreshold = 0.001f;
        private int _sceneReflectionRayTaskBudget = 65_536;
        private int _sceneReflectionSsrSampleBudget = 4_194_304;
        private int _thickTransmissionRayTaskBudget = 262_144;
        private int _thickTransmissionMaximumInterfaces =
            BoundedDielectricMediaStack.MaximumInterfaces;
        private int _thickTransmissionMaximumMediaDepth =
            BoundedDielectricMediaStack.MaximumDepth;
        private int _thickTransmissionMaximumCandidatesPerInterface =
            BoundedDielectricMediaStack.MaximumCandidatesPerInterface;
        private float _thickTransmissionMaximumDistance = 100f;
        private ulong _thickTransmissionMemoryBudgetBytes =
            128UL * 1024UL * 1024UL;

        public bool Enabled { get; set; } = true;
        public TransparencyMode Mode { get; set; } = TransparencyMode.SortedAlphaBlend;
        public TransparencyDebugView DebugView { get; set; } = TransparencyDebugView.None;
        public bool ReceiveShadows { get; set; } = true;
        public bool ReceiveGlobalIllumination { get; set; } = true;
        public bool SampleReflections { get; set; } = true;
        public bool SortPerMeshlet { get; set; } = true;
        /// <summary>
        /// Qualification candidate for bounded transparent material runs. The
        /// universal one-list draw remains the default and rollback path.
        /// </summary>
        public bool PipelinePartitioningEnabled { get; set; }
        public ThickTransmissionMode ThickTransmissionMode { get; set; } =
            ThickTransmissionMode.RayQuery;
        public DispersionMode DispersionMode { get; set; } = DispersionMode.Off;

        /// <summary>
        /// Maximum transparent fragments admitted to the scene-reflection
        /// ray-query recovery path in one frame. SSR and analytic fallbacks
        /// remain available when this bounded budget is zero or exhausted.
        /// </summary>
        public int SceneReflectionRayTaskBudget
        {
            get => _sceneReflectionRayTaskBudget;
            set => _sceneReflectionRayTaskBudget = Math.Clamp(
                value,
                0,
                MaximumSceneReflectionRayTaskBudget);
        }

        /// <summary>
        /// Maximum Hi-Z depth samples reserved by transparent scene-reflection
        /// SSR in one frame. Each admitted fragment reserves its complete
        /// worst-case march before issuing its first depth sample.
        /// </summary>
        public int SceneReflectionSsrSampleBudget
        {
            get => _sceneReflectionSsrSampleBudget;
            set => _sceneReflectionSsrSampleBudget = Math.Clamp(
                value,
                0,
                MaximumSceneReflectionSsrSampleBudget);
        }

        public int ThickTransmissionRayTaskBudget
        {
            get => _thickTransmissionRayTaskBudget;
            set => _thickTransmissionRayTaskBudget =
                Math.Clamp(
                    value,
                    0,
                    GPUForwardPushConstants.MaximumThickTransmissionRayTaskBudget);
        }

        public int ThickTransmissionMaximumInterfaces
        {
            get => _thickTransmissionMaximumInterfaces;
            set => _thickTransmissionMaximumInterfaces = Math.Clamp(
                value, 1, BoundedDielectricMediaStack.MaximumInterfaces);
        }

        public int ThickTransmissionMaximumMediaDepth
        {
            get => _thickTransmissionMaximumMediaDepth;
            set => _thickTransmissionMaximumMediaDepth = Math.Clamp(
                value, 1, BoundedDielectricMediaStack.MaximumDepth);
        }

        public int ThickTransmissionMaximumCandidatesPerInterface
        {
            get => _thickTransmissionMaximumCandidatesPerInterface;
            set => _thickTransmissionMaximumCandidatesPerInterface = Math.Clamp(
                value, 1,
                BoundedDielectricMediaStack.MaximumCandidatesPerInterface);
        }

        public float ThickTransmissionMaximumDistance
        {
            get => _thickTransmissionMaximumDistance;
            set => _thickTransmissionMaximumDistance = Clamp(value, 0.1f, 10_000f);
        }

        public ulong ThickTransmissionMemoryBudgetBytes
        {
            get => _thickTransmissionMemoryBudgetBytes;
            set => _thickTransmissionMemoryBudgetBytes = Math.Clamp(
                value, 16UL * 1024UL * 1024UL,
                2UL * 1024UL * 1024UL * 1024UL);
        }

        public int MaxTransparentMeshlets
        {
            get => _maxTransparentMeshlets;
            set => _maxTransparentMeshlets = value < 0 ? 0 : value;
        }

        public float AlphaDiscardThreshold
        {
            get => _alphaDiscardThreshold;
            set => _alphaDiscardThreshold = Clamp(value, 0.0f, 0.05f);
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min)
                return min;
            return value > max ? max : value;
        }
    }

    public sealed class DecalSettings
    {
        private float _geometryDepthBias = 0.0005f;
        private float _geometrySlopeScaledDepthBias;
        private int _maxProjectedDecals = 256;
        private int _maxProjectedDecalsPerTile = 64;
        private int _maxProjectedDecalsPerPixel = 8;

        public bool GeometryDecalsEnabled { get; set; } = true;
        public bool ProjectedDecalsEnabled { get; set; }
        /// <summary>
        /// Controls shadow sampling for geometry-decal receivers independently
        /// from ordinary transparent materials. Geometry decals share the
        /// transparent draw path, so folding this into TransparencySettings
        /// makes a controlled decal-only capture impossible.
        /// </summary>
        public bool ReceiveShadows { get; set; } = true;
        public bool ReceiveGlobalIllumination { get; set; } = true;
        /// <summary>
        /// Optional material-index isolation used by deterministic performance
        /// captures. A negative value renders every decal material.
        /// </summary>
        public int IsolatedMaterialIndex { get; set; } = -1;
        public DecalDebugView DebugView { get; set; } = DecalDebugView.None;

        public float GeometryDepthBias
        {
            get => _geometryDepthBias;
            set => _geometryDepthBias = Clamp(value, 0.0f, 0.01f);
        }

        public float GeometrySlopeScaledDepthBias
        {
            get => _geometrySlopeScaledDepthBias;
            set => _geometrySlopeScaledDepthBias = Clamp(value, 0.0f, 4.0f);
        }

        public int MaxProjectedDecals
        {
            get => _maxProjectedDecals;
            set => _maxProjectedDecals = Clamp(value, 0, 4096);
        }

        public int MaxProjectedDecalsPerTile
        {
            get => _maxProjectedDecalsPerTile;
            set => _maxProjectedDecalsPerTile = Clamp(value, 0, 256);
        }

        public int MaxProjectedDecalsPerPixel
        {
            get => _maxProjectedDecalsPerPixel;
            set => _maxProjectedDecalsPerPixel = Clamp(value, 0, 32);
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
                return min;
            return value > max ? max : value;
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min)
                return min;
            return value > max ? max : value;
        }
    }

    public sealed class BloomSettings
    {
        private float _intensity = 0.08f;
        private float _threshold = 1.0f;
        private float _knee = 0.5f;
        private float _radius = 0.65f;
        private int _mipCount = 6;
        private int _debugMipLevel;

        public bool Enabled { get; set; } = true;

        public float Intensity
        {
            get => _intensity;
            set => _intensity = Clamp(value, 0.0f, 2.0f);
        }

        public float Threshold
        {
            get => _threshold;
            set => _threshold = Clamp(value, 0.0f, 20.0f);
        }

        public float Knee
        {
            get => _knee;
            set => _knee = Clamp(value, 0.0f, 1.0f);
        }

        public float Radius
        {
            get => _radius;
            set => _radius = Clamp(value, 0.0f, 1.0f);
        }

        public int MipCount
        {
            get => _mipCount;
            set => _mipCount = value < 1 ? 1 : value > 8 ? 8 : value;
        }

        public BloomDebugView DebugView { get; set; } = BloomDebugView.None;

        public int DebugMipLevel
        {
            get => _debugMipLevel;
            set => _debugMipLevel = value < 0 ? 0 : value > 7 ? 7 : value;
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min)
                return min;
            return value > max ? max : value;
        }
    }

    public sealed class EnvironmentSettings
    {
        private float _skyIntensity = 1.0f;
        private float _diffuseIntensity = 1.0f;
        private float _specularIntensity = 1.0f;
        private float _turbidity = 3.0f;
        private Vector3 _groundAlbedo = new(0.20f);
        private float _sunAngularDiameterDegrees = 0.53f;
        private float _moonAngularDiameterDegrees = 0.52f;
        private float _timeOfDayHours = 14.0f;
        private float _latitudeDegrees = 59.9139f;
        private int _dayOfYear = 172;
        private float _northOffsetDegrees;
        private float _timeScale = 60.0f;
        private Vector3 _directSunDirection = NormalizeDirectSunDirection(
            new Vector3(-0.3f, 0.72f, 0.62f));
        private float _atmosphereIntensity = 1.0f;
        private float _solarIrradianceScale = 14.0f;
        private float _moonIrradianceScale = 0.12f;
        private float _starIntensity = 0.025f;
        private float _airglowIntensity = 0.025f;
        private float _giSunStepDegrees = 0.25f;
        private float _giTargetSourceSweepSeconds = 8.0f;
        private int _specularPrefilterMipsPerFrame = 1;
        private int _specularPrefilterTransitionFrames = 8;
        private float _rotationRadians;
        private uint _environmentSize = 1024;
        private uint _irradianceSize = 64;
        private uint _prefilteredSize = 128;
        private uint _brdfLutSize = 256;
        private int _debugMipLevel;

        public bool Enabled { get; set; } = true;
        public EnvironmentSourceKind SourceKind { get; set; } = EnvironmentSourceKind.ProceduralSky;
        public string? SourcePath { get; set; }
        public EnvironmentTexturePrecision TexturePrecision { get; set; } = EnvironmentTexturePrecision.Float16;
        public ProceduralSkySunDriver SunDriver { get; set; } =
            ProceduralSkySunDriver.SceneDirectionalLight;
        public bool AnimateTimeOfDay { get; set; }

        /// <summary>
        /// Runtime constant-radiance safety value derived from the active sky's
        /// diffuse SH. It is deliberately not serialized: normal rendering and
        /// probe misses use the environment GPU contract, while this value keeps
        /// exceptional/no-resource paths physically consistent with that contract.
        /// </summary>
        [JsonIgnore]
        public Vector3 TransportFallbackRadiance { get; internal set; }

        public float Turbidity
        {
            get => _turbidity;
            set => _turbidity = Clamp(value, 1.0f, 10.0f);
        }

        public Vector3 GroundAlbedo
        {
            get => _groundAlbedo;
            set => _groundAlbedo = new Vector3(
                Clamp(value.X, 0.0f, 1.0f),
                Clamp(value.Y, 0.0f, 1.0f),
                Clamp(value.Z, 0.0f, 1.0f));
        }

        public float SunAngularDiameterDegrees
        {
            get => _sunAngularDiameterDegrees;
            set => _sunAngularDiameterDegrees = Clamp(value, 0.0f, 2.0f);
        }

        public float MoonAngularDiameterDegrees
        {
            get => _moonAngularDiameterDegrees;
            set => _moonAngularDiameterDegrees = Clamp(value, 0.1f, 2.0f);
        }

        public float TimeOfDayHours
        {
            get => _timeOfDayHours;
            set => _timeOfDayHours = Clamp(value, 0.0f, 24.0f);
        }

        public float LatitudeDegrees
        {
            get => _latitudeDegrees;
            set => _latitudeDegrees = Clamp(value, -90.0f, 90.0f);
        }

        public int DayOfYear
        {
            get => _dayOfYear;
            set => _dayOfYear = Math.Clamp(value, 1, 366);
        }

        public float NorthOffsetDegrees
        {
            get => _northOffsetDegrees;
            set => _northOffsetDegrees = Clamp(value, -360.0f, 360.0f);
        }

        /// <summary>Simulated solar seconds advanced per real second.</summary>
        public float TimeScale
        {
            get => _timeScale;
            set => _timeScale = Clamp(value, 0.0f, 86_400.0f);
        }

        /// <summary>World-space direction from the scene toward the sun.</summary>
        public Vector3 DirectSunDirection
        {
            get => _directSunDirection;
            set => _directSunDirection = NormalizeDirectSunDirection(value);
        }

        public float AtmosphereIntensity
        {
            get => _atmosphereIntensity;
            set => _atmosphereIntensity = Clamp(value, 0.0f, 16.0f);
        }

        public float SolarIrradianceScale
        {
            get => _solarIrradianceScale;
            set => _solarIrradianceScale = Clamp(value, 0.0f, 128.0f);
        }

        public float MoonIrradianceScale
        {
            get => _moonIrradianceScale;
            set => _moonIrradianceScale = Clamp(value, 0.0f, 8.0f);
        }

        public float StarIntensity
        {
            get => _starIntensity;
            set => _starIntensity = Clamp(value, 0.0f, 2.0f);
        }

        public float AirglowIntensity
        {
            get => _airglowIntensity;
            set => _airglowIntensity = Clamp(value, 0.0f, 2.0f);
        }

        public float GiSunStepDegrees
        {
            get => _giSunStepDegrees;
            set => _giSunStepDegrees = Clamp(value, 0.02f, 5.0f);
        }

        public float GiTargetSourceSweepSeconds
        {
            get => _giTargetSourceSweepSeconds;
            set => _giTargetSourceSweepSeconds = Clamp(value, 0.25f, 120.0f);
        }

        public int SpecularPrefilterMipsPerFrame
        {
            get => _specularPrefilterMipsPerFrame;
            set => _specularPrefilterMipsPerFrame = Math.Clamp(value, 1, 5);
        }

        public int SpecularPrefilterTransitionFrames
        {
            get => _specularPrefilterTransitionFrames;
            set => _specularPrefilterTransitionFrames = Math.Clamp(value, 1, 120);
        }

        public float SkyIntensity
        {
            get => _skyIntensity;
            set => _skyIntensity = Clamp(value, 0.0f, 16.0f);
        }

        public float DiffuseIntensity
        {
            get => _diffuseIntensity;
            set => _diffuseIntensity = Clamp(value, 0.0f, 16.0f);
        }

        public float SpecularIntensity
        {
            get => _specularIntensity;
            set => _specularIntensity = Clamp(value, 0.0f, 16.0f);
        }

        public float RotationRadians
        {
            get => _rotationRadians;
            set => _rotationRadians = float.IsFinite(value) ? value : 0.0f;
        }

        public uint EnvironmentSize
        {
            get => _environmentSize;
            set => _environmentSize = ClampPowerOfTwo(value, 256, 4096);
        }

        public uint IrradianceSize
        {
            get => _irradianceSize;
            set => _irradianceSize = ClampPowerOfTwo(value, 16, 256);
        }

        public uint PrefilteredSize
        {
            get => _prefilteredSize;
            set => _prefilteredSize = ClampPowerOfTwo(value, 64, 1024);
        }

        public uint BrdfLutSize
        {
            get => _brdfLutSize;
            set => _brdfLutSize = ClampPowerOfTwo(value, 128, 512);
        }

        public EnvironmentDebugView DebugView { get; set; } = EnvironmentDebugView.None;

        public int DebugMipLevel
        {
            get => _debugMipLevel;
            set => _debugMipLevel = value < 0 ? 0 : value > 15 ? 15 : value;
        }

        internal void ClampDebugMipLevel(uint mipCount)
        {
            int maxMip = mipCount == 0 ? 0 : checked((int)mipCount - 1);
            if (_debugMipLevel > maxMip)
                _debugMipLevel = maxMip;
        }

        private static uint ClampPowerOfTwo(uint value, uint min, uint max)
        {
            if (value < min)
                return min;
            if (value > max)
                return max;

            uint rounded = 1;
            while (rounded < value)
                rounded <<= 1;
            return rounded;
        }

        private static float Clamp(float value, float min, float max)
        {
            if (!float.IsFinite(value))
                return min;
            if (value < min)
                return min;
            return value > max ? max : value;
        }

        private static Vector3 NormalizeDirectSunDirection(Vector3 value)
        {
            float lengthSquared = value.LengthSquared();
            if (!float.IsFinite(value.X) ||
                !float.IsFinite(value.Y) ||
                !float.IsFinite(value.Z) ||
                !float.IsFinite(lengthSquared) ||
                lengthSquared <= 0.000001f)
            {
                return new Vector3(-0.3f, 0.72f, 0.62f).Normalized();
            }

            // Preserve an already-normalized serialized value bit-for-bit. This
            // keeps save/load and quality-tier rollback exact while still making
            // arbitrary authored directions safe for the atmosphere model.
            return MathF.Abs(lengthSquared - 1.0f) <= 1.0e-6f
                ? value
                : value.Normalized();
        }
    }

    public sealed class ReflectionSettings
    {
        public const int ShaderMaxProbesPerPixel = 4;
        public const uint ReceiverPayloadAbiVersion = 4;

        private int _maxProbes = 8;
        private int _maxProbesPerPixel = 2;
        private uint _probeResolution = 128;
        private float _intensity = 1.0f;
        private float _globalFallbackIntensity = 1.0f;
        private int _maxProbeCapturesPerFrame;
        private int _maxConcurrentProbeCaptures = 1;
        private int _maxProbeCaptureFacesPerFrame = 1;
        private int _maxProbePrefilterMipsPerFrame = 1;
        private int _reflectionCaptureGpuBudgetMicroseconds = 500;
        private float _minimumEnvironmentRecaptureIntervalSeconds = 8.0f;
        private float _maximumEnvironmentCaptureAgeSeconds = 60.0f;
        private int _reflectionCaptureRetryLimit = 3;
        private int _debugProbeIndex;
        private int _debugCubemapFace;
        private int _debugMipLevel;
        private float _ssrFullResolutionRoughness = 0.2f;
        private float _ssrHalfResolutionRoughness = 0.5f;
        private float _ssrQuarterResolutionRoughness = 0.8f;
        private int _ssrMaxSteps = 64;
        private float _ssrMaxDistance = 75.0f;
        private float _ssrConfidenceThreshold = 0.75f;
        private float _rayQueryPixelBudgetFraction = 0.0078125f;
        private int _rayQueryHitLightLimit = 2;
        private int _temporalHistoryLength = 16;
        private int _spatialFilterPassCount = 2;

        public bool Enabled { get; set; } = true;
        public ReflectionMode Mode { get; set; } = ReflectionMode.StaticProbes;

        public int MaxProbes
        {
            get => _maxProbes;
            set => _maxProbes = value < 0 ? 0 : value > 256 ? 256 : value;
        }

        public int MaxProbesPerPixel
        {
            get => _maxProbesPerPixel;
            set => _maxProbesPerPixel = value < 1 ? 1 : value > ShaderMaxProbesPerPixel ? ShaderMaxProbesPerPixel : value;
        }

        public uint ProbeResolution
        {
            get => _probeResolution;
            set => _probeResolution = ClampPowerOfTwo(value, 64, 1024);
        }

        public float Intensity
        {
            get => _intensity;
            set => _intensity = Clamp(value, 0.0f, 4.0f);
        }

        public float GlobalFallbackIntensity
        {
            get => _globalFallbackIntensity;
            set => _globalFallbackIntensity = Clamp(value, 0.0f, 4.0f);
        }

        public bool BoxProjectionEnabled { get; set; } = true;
        public bool ProbeBlendingEnabled { get; set; } = true;
        public bool CaptureOnLoad { get; set; }

        public int MaxProbeCapturesPerFrame
        {
            get => _maxProbeCapturesPerFrame;
            set => _maxProbeCapturesPerFrame = value < 0 ? 0 : value > 4 ? 4 : value;
        }

        public int MaxConcurrentProbeCaptures { get => _maxConcurrentProbeCaptures; set => _maxConcurrentProbeCaptures = Math.Clamp(value, 1, 4); }
        public int MaxProbeCaptureFacesPerFrame { get => _maxProbeCaptureFacesPerFrame; set => _maxProbeCaptureFacesPerFrame = Math.Clamp(value, 0, 6); }
        public int MaxProbePrefilterMipsPerFrame { get => _maxProbePrefilterMipsPerFrame; set => _maxProbePrefilterMipsPerFrame = Math.Clamp(value, 0, 16); }
        public int ReflectionCaptureGpuBudgetMicroseconds { get => _reflectionCaptureGpuBudgetMicroseconds; set => _reflectionCaptureGpuBudgetMicroseconds = Math.Clamp(value, 0, 1_000); }
        public float MinimumEnvironmentRecaptureIntervalSeconds { get => _minimumEnvironmentRecaptureIntervalSeconds; set => _minimumEnvironmentRecaptureIntervalSeconds = Math.Clamp(value, 0.0f, 3600.0f); }
        public float MaximumEnvironmentCaptureAgeSeconds { get => _maximumEnvironmentCaptureAgeSeconds; set => _maximumEnvironmentCaptureAgeSeconds = Math.Clamp(value, 1.0f, 86_400.0f); }
        public bool CaptureIncludesDdgi { get; set; }
        public int ReflectionCaptureRetryLimit { get => _reflectionCaptureRetryLimit; set => _reflectionCaptureRetryLimit = Math.Clamp(value, 0, 16); }
        public int ReflectionCaptureRetryBackoffFrames { get; set; } = 30;

        public float SsrFullResolutionRoughness
        {
            get => _ssrFullResolutionRoughness;
            set => _ssrFullResolutionRoughness = Clamp(value, 0.0f, 1.0f);
        }

        public float SsrHalfResolutionRoughness
        {
            get => _ssrHalfResolutionRoughness;
            set => _ssrHalfResolutionRoughness = Clamp(value, 0.0f, 1.0f);
        }

        public float SsrQuarterResolutionRoughness
        {
            get => _ssrQuarterResolutionRoughness;
            set => _ssrQuarterResolutionRoughness = Clamp(value, 0.0f, 1.0f);
        }

        public int SsrMaxSteps
        {
            get => _ssrMaxSteps;
            set => _ssrMaxSteps = Math.Clamp(value, 8, 256);
        }

        public float SsrMaxDistance
        {
            get => _ssrMaxDistance;
            set => _ssrMaxDistance = Clamp(value, 1.0f, 1000.0f);
        }

        public float SsrConfidenceThreshold
        {
            get => _ssrConfidenceThreshold;
            set => _ssrConfidenceThreshold = Clamp(value, 0.0f, 1.0f);
        }

        /// <summary>Maximum fraction of render-resolution pixels traced by ray query each frame.</summary>
        public float RayQueryPixelBudgetFraction
        {
            get => _rayQueryPixelBudgetFraction;
            set => _rayQueryPixelBudgetFraction = Clamp(value, 0.0f, 1.0f);
        }

        public int RayQueryHitLightLimit
        {
            get => _rayQueryHitLightLimit;
            set => _rayQueryHitLightLimit = Math.Clamp(value, 0, 16);
        }

        public int TemporalHistoryLength
        {
            get => _temporalHistoryLength;
            set => _temporalHistoryLength = Math.Clamp(value, 1, 64);
        }

        public int SpatialFilterPassCount
        {
            get => _spatialFilterPassCount;
            set => _spatialFilterPassCount = Math.Clamp(value, 0, 8);
        }

        /// <summary>
        /// Runs the material-aware DDGI reflection base at one gather per
        /// receiver pixel. This is an exact diagnostic oracle; production uses
        /// edge-aware grouped gathers while SSR and ray queries retain their
        /// independently budgeted sharp detail.
        /// </summary>
        public bool DdgiReflectionFullResolutionOracle { get; set; }

        public void ApplyHybridQualityBudget(RenderQualityPreset preset)
        {
            DdgiReflectionFullResolutionOracle = false;
            switch (preset)
            {
                case RenderQualityPreset.Medium:
                    Mode = ReflectionMode.StaticProbesAndSsr;
                    SsrFullResolutionRoughness = 0.15f;
                    SsrHalfResolutionRoughness = 0.45f;
                    SsrQuarterResolutionRoughness = 0.70f;
                    SsrMaxSteps = 48;
                    SsrMaxDistance = 50.0f;
                    RayQueryPixelBudgetFraction = 0.0f;
                    RayQueryHitLightLimit = 0;
                    TemporalHistoryLength = 8;
                    SpatialFilterPassCount = 2;
                    break;
                case RenderQualityPreset.Ultra:
                    Mode = ReflectionMode.HybridRayQuery;
                    SsrFullResolutionRoughness = 0.30f;
                    SsrHalfResolutionRoughness = 0.65f;
                    SsrQuarterResolutionRoughness = 0.90f;
                    SsrMaxSteps = 96;
                    SsrMaxDistance = 150.0f;
                    // Ray-query recovery shades the hit and may trace shadow
                    // visibility. Keep the budget bounded enough for dense
                    // production scenes; temporal accumulation fills the
                    // rotating sparse sample set over subsequent frames.
                    RayQueryPixelBudgetFraction = 0.015625f;
                    RayQueryHitLightLimit = 4;
                    TemporalHistoryLength = 32;
                    SpatialFilterPassCount = 4;
                    break;
                default:
                    Mode = ReflectionMode.HybridRayQuery;
                    SsrFullResolutionRoughness = 0.20f;
                    SsrHalfResolutionRoughness = 0.50f;
                    SsrQuarterResolutionRoughness = 0.80f;
                    SsrMaxSteps = 64;
                    SsrMaxDistance = 75.0f;
                    RayQueryPixelBudgetFraction = 0.0078125f;
                    RayQueryHitLightLimit = 2;
                    TemporalHistoryLength = 16;
                    SpatialFilterPassCount = 2;
                    break;
            }
        }

        public ReflectionDebugView DebugView { get; set; } = ReflectionDebugView.None;

        public int DebugProbeIndex
        {
            get => _debugProbeIndex;
            set => _debugProbeIndex = value < 0 ? 0 : value;
        }

        public int DebugCubemapFace
        {
            get => _debugCubemapFace;
            set => _debugCubemapFace = value < 0 ? 0 : value > 5 ? 5 : value;
        }

        public int DebugMipLevel
        {
            get => _debugMipLevel;
            set => _debugMipLevel = value < 0 ? 0 : value > 15 ? 15 : value;
        }

        internal void ClampDebugResources(int activeProbeCount, uint mipCount)
        {
            int maxProbeIndex = activeProbeCount <= 0 ? 0 : activeProbeCount - 1;
            if (_debugProbeIndex > maxProbeIndex)
                _debugProbeIndex = maxProbeIndex;

            int maxMip = mipCount == 0 ? 0 : checked((int)mipCount - 1);
            if (_debugMipLevel > maxMip)
                _debugMipLevel = maxMip;
        }

        private static uint ClampPowerOfTwo(uint value, uint min, uint max)
        {
            if (value < min)
                return min;
            if (value > max)
                return max;

            uint rounded = 1;
            while (rounded < value)
                rounded <<= 1;
            return rounded;
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min)
                return min;
            return value > max ? max : value;
        }
    }

    public sealed class AmbientOcclusionSettings
    {
        private AmbientOcclusionMode _mode = AmbientOcclusionMode.Ssao;
        private float _resolutionScale = 0.5f;
        private float _radius = 0.75f;
        private float _intensity = 1.0f;
        private float _bias = 0.03f;
        private float _power = 1.2f;
        private int _sampleCount = 16;
        private int _blurRadius = 2;
        private float _depthSigma = 2.0f;
        private float _normalSigma = 32.0f;
        private GtaoQualityPreset _gtaoQualityPreset =
            GtaoQualityPreset.Balanced;
        private float _gtaoThickness = 0.15f;
        private float _gtaoFalloff = 1.0f;
        private AmbientOcclusionBentNormalMode _bentNormalMode =
            AmbientOcclusionBentNormalMode.Off;

        public bool Enabled { get; set; } = true;
        public AmbientOcclusionMode Mode
        {
            get => _mode;
            set => _mode = Enum.IsDefined(value)
                ? value
                : AmbientOcclusionMode.Ssao;
        }

        public GtaoQualityPreset GtaoQualityPreset
        {
            get => _gtaoQualityPreset;
            set => _gtaoQualityPreset = Enum.IsDefined(value)
                ? value
                : GtaoQualityPreset.Balanced;
        }

        public float GtaoThickness
        {
            get => _gtaoThickness;
            set => _gtaoThickness = Clamp(value, 0.01f, 1.0f);
        }

        public float GtaoFalloff
        {
            get => _gtaoFalloff;
            set => _gtaoFalloff = Clamp(value, 0.1f, 4.0f);
        }

        public AmbientOcclusionBentNormalMode BentNormalMode
        {
            get => _bentNormalMode;
            set => _bentNormalMode = Enum.IsDefined(value)
                ? value
                : AmbientOcclusionBentNormalMode.Off;
        }

        public AmbientOcclusionBentNormalMode EffectiveBentNormalMode =>
            Enabled && Mode == AmbientOcclusionMode.Gtao
                ? BentNormalMode
                : AmbientOcclusionBentNormalMode.Off;

        public int EffectiveGtaoDirectionCount => GtaoQualityPreset switch
        {
            GtaoQualityPreset.Low => 2,
            GtaoQualityPreset.High => 6,
            _ => 4
        };

        public int EffectiveGtaoStepCount => GtaoQualityPreset switch
        {
            GtaoQualityPreset.Low => 4,
            GtaoQualityPreset.High => 8,
            _ => 6
        };

        public float ResolutionScale
        {
            get => _resolutionScale;
            set => _resolutionScale = value <= 0.375f ? 0.25f : value <= 0.75f ? 0.5f : 1.0f;
        }

        public float Radius
        {
            get => _radius;
            set => _radius = Clamp(value, 0.05f, 5.0f);
        }

        public float Intensity
        {
            get => _intensity;
            set => _intensity = Clamp(value, 0.0f, 4.0f);
        }

        public float Bias
        {
            get => _bias;
            set => _bias = Clamp(value, 0.0f, 0.5f);
        }

        public float Power
        {
            get => _power;
            set => _power = Clamp(value, 0.25f, 4.0f);
        }

        public int SampleCount
        {
            get => _sampleCount;
            set => _sampleCount = value <= 6 ? 4 : value <= 12 ? 8 : value <= 24 ? 16 : 32;
        }

        public int BlurRadius
        {
            get => _blurRadius;
            set => _blurRadius = value < 0 ? 0 : value > 4 ? 4 : value;
        }

        public float DepthSigma
        {
            get => _depthSigma;
            set => _depthSigma = Clamp(value, 0.1f, 16.0f);
        }

        public float NormalSigma
        {
            get => _normalSigma;
            set => _normalSigma = Clamp(value, 1.0f, 128.0f);
        }

        public bool UseSceneNormals { get; set; }
        public AmbientOcclusionDebugView DebugView { get; set; } = AmbientOcclusionDebugView.None;

        private static float Clamp(float value, float min, float max)
        {
            if (value < min)
                return min;
            return value > max ? max : value;
        }
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
        public const ulong DefaultDdgiAtlasMemoryBudgetBytes = 192UL * 1024UL * 1024UL;
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
        // The certified single-sweep Jacobi path is the correctness fallback.
        // Accelerated cached sweeps remain explicitly selectable for hardware
        // qualification, but are not a production default after the
        // 2026-08-27 fixed-point-tail regression.
        private bool _simpleDdgiTransportAccelerationEnabled;
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
            // Preserve the canonical Jacobi solve for every production tier.
            // Tail-accelerated benchmark variants opt back in explicitly.
            SimpleDdgiTransportAccelerationEnabled = false;
            SimpleDdgiTransportTailCertificationEnabled = true;
            bool highTier = tier is
                DdgiQualityTier.DdgiHigh or DdgiQualityTier.DdgiUltra;
            SimpleDdgiRefinementBricksEnabled = highTier;
            SimpleDdgiRefinementMaximumBricks = tier == DdgiQualityTier.DdgiUltra
                ? 4
                : tier == DdgiQualityTier.DdgiHigh
                    ? 2
                    : 0;
            SimpleDdgiNearVisibilitySidecarEnabled = highTier;
            SimpleDdgiNearVisibilitySidecarMemoryBudgetBytes = tier ==
                DdgiQualityTier.DdgiUltra
                    ? 96UL * 1024UL * 1024UL
                    : tier == DdgiQualityTier.DdgiHigh
                        ? 64UL * 1024UL * 1024UL
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
            SimpleDdgiDirectionalRadianceMode = highTier
                ? SimpleDdgiDirectionalRadianceMode.L2
                : SimpleDdgiDirectionalRadianceMode.Off;
            // Directional L2 is the probe-free glossy base for both shipping
            // DDGI tiers. High evaluates it at the receiver only; Ultra also
            // feeds one bounded glossy bounce back into transport.
            SimpleDdgiGlossyTransportMode = tier switch
            {
                DdgiQualityTier.DdgiUltra =>
                    SimpleDdgiGlossyTransportMode.OneBounce,
                DdgiQualityTier.DdgiHigh =>
                    SimpleDdgiGlossyTransportMode.ReceiverOnly,
                _ => SimpleDdgiGlossyTransportMode.Off
            };
            SimpleDdgiDirectionalFogEnabled = highTier;
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
                _ => (262_144, 8, 1, 192UL * 1024UL * 1024UL)
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

    public sealed class AntiAliasingSettings
    {
        private float _fxaaContrastThreshold = 0.125f;
        private float _fxaaRelativeThreshold = 0.166f;
        private float _fxaaSubpixelBlending = 0.75f;
        private int _jitterSampleCount = 8;
        private float _taaFeedbackMin = 0.85f;
        private float _taaFeedbackMax = 0.95f;
        private float _taaVelocityRejectionScale = 1.0f;

        public AntiAliasingMode Mode { get; set; } = AntiAliasingMode.SmaaMedium;
        public AntiAliasingDebugView DebugView { get; set; } = AntiAliasingDebugView.None;

        public float FxaaContrastThreshold
        {
            get => _fxaaContrastThreshold;
            set => _fxaaContrastThreshold = Clamp(value, 0.0312f, 0.333f);
        }

        public float FxaaRelativeThreshold
        {
            get => _fxaaRelativeThreshold;
            set => _fxaaRelativeThreshold = Clamp(value, 0.063f, 0.333f);
        }

        public float FxaaSubpixelBlending
        {
            get => _fxaaSubpixelBlending;
            set => _fxaaSubpixelBlending = Clamp(value, 0.0f, 1.0f);
        }

        public bool SmaaPredicationEnabled { get; set; }
        public bool JitterEnabled { get; set; } = true;

        public int JitterSampleCount
        {
            get => _jitterSampleCount;
            set => _jitterSampleCount = value <= 3 ? 2 : value <= 6 ? 4 : value <= 12 ? 8 : 16;
        }

        public float TaaFeedbackMin
        {
            get => _taaFeedbackMin;
            set
            {
                _taaFeedbackMin = Clamp(value, 0.5f, 0.98f);
                if (_taaFeedbackMax < _taaFeedbackMin)
                    _taaFeedbackMax = _taaFeedbackMin;
            }
        }

        public float TaaFeedbackMax
        {
            get => _taaFeedbackMax;
            set => _taaFeedbackMax = Clamp(value, _taaFeedbackMin, 0.99f);
        }

        public float TaaVelocityRejectionScale
        {
            get => _taaVelocityRejectionScale;
            set => _taaVelocityRejectionScale = !float.IsFinite(value)
                ? 1.0f
                : Clamp(value, 0.0f, 64.0f);
        }

        public AntiAliasingMode EffectiveMode => Mode;
        public int EffectiveSmaaSpatialSampleCount => IsSmaaMode(EffectiveMode) ? 1 : 0;
        public bool EffectiveSmaaUsesSpatialMultisampling => false;
        public float EffectiveSmaaResolutionScale => GetSmaaPreset(EffectiveMode).ResolutionScale;
        public float EffectiveSmaaThreshold => GetSmaaPreset(EffectiveMode).Threshold;
        public int EffectiveSmaaMaxSearchSteps => GetSmaaPreset(EffectiveMode).MaxSearchSteps;
        public int EffectiveSmaaMaxSearchStepsDiagonal => GetSmaaPreset(EffectiveMode).MaxSearchStepsDiagonal;
        public float EffectiveSmaaCornerRounding => GetSmaaPreset(EffectiveMode).CornerRounding;
        public bool EffectiveSmaaDiagonalEnabled => GetSmaaPreset(EffectiveMode).MaxSearchStepsDiagonal > 0;
        public bool EffectiveSmaaCornerEnabled => GetSmaaPreset(EffectiveMode).CornerRounding > 0.0f;
        public int EffectiveSmaaQuality => GetSmaaPreset(EffectiveMode).Quality;

        public static bool IsSmaaMode(AntiAliasingMode mode)
        {
            return mode is AntiAliasingMode.SmaaLow or
                AntiAliasingMode.SmaaMedium or
                AntiAliasingMode.SmaaHigh;
        }

        public static float GetSmaaResolutionScale(AntiAliasingMode mode) =>
            GetSmaaPreset(mode).ResolutionScale;

        private static SmaaPreset GetSmaaPreset(AntiAliasingMode mode)
        {
            return mode switch
            {
                AntiAliasingMode.SmaaLow => new SmaaPreset(0, 0.50f, 0.15f, 4, 0, 0.0f),
                AntiAliasingMode.SmaaMedium => new SmaaPreset(1, 0.75f, 0.10f, 8, 0, 0.0f),
                AntiAliasingMode.SmaaHigh => new SmaaPreset(2, 1.00f, 0.10f, 16, 8, 25.0f),
                _ => new SmaaPreset(0, 1.00f, 0.0f, 0, 0, 0.0f)
            };
        }

        private readonly struct SmaaPreset
        {
            public SmaaPreset(
                int quality,
                float resolutionScale,
                float threshold,
                int maxSearchSteps,
                int maxSearchStepsDiagonal,
                float cornerRounding)
            {
                Quality = quality;
                ResolutionScale = resolutionScale;
                Threshold = threshold;
                MaxSearchSteps = maxSearchSteps;
                MaxSearchStepsDiagonal = maxSearchStepsDiagonal;
                CornerRounding = cornerRounding;
            }

            public int Quality { get; }
            public float ResolutionScale { get; }
            public float Threshold { get; }
            public int MaxSearchSteps { get; }
            public int MaxSearchStepsDiagonal { get; }
            public float CornerRounding { get; }
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min)
                return min;
            return value > max ? max : value;
        }
    }

    public sealed class FogSettings
    {
        private float _colorBlend = 0.5f;
        private float _density = 0.015f;
        private float _startDistance = 5.0f;
        private float _endDistance = 250.0f;
        private float _heightFalloff = 0.12f;
        private float _heightDensity = 0.04f;
        private float _maxOpacity = 0.85f;
        private float _directionalInscatteringIntensity = 0.35f;
        private float _directionalInscatteringExponent = 8.0f;

        public bool Enabled { get; set; } = true;
        public FogTechnique Technique { get; set; } = FogTechnique.Auto;
        public FogMode Mode { get; set; } = FogMode.DistanceAndHeight;
        public FogColorMode ColorMode { get; set; } = FogColorMode.SkyAndConstantBlend;
        public Vector3 Color { get; set; } = new(0.62f, 0.72f, 0.82f);

        public float ColorBlend
        {
            get => _colorBlend;
            set => _colorBlend = Clamp(value, 0.0f, 1.0f);
        }

        public float Density
        {
            get => _density;
            set => _density = Clamp(value, 0.0f, 1.0f);
        }

        public float StartDistance
        {
            get => _startDistance;
            set
            {
                _startDistance = Clamp(value, 0.0f, 10000.0f);
                if (_endDistance <= _startDistance)
                    _endDistance = _startDistance + 0.01f;
            }
        }

        public float EndDistance
        {
            get => _endDistance;
            set => _endDistance = Math.Max(_startDistance + 0.01f, Clamp(value, 0.01f, 10000.01f));
        }

        public float Height { get; set; }

        public float HeightFalloff
        {
            get => _heightFalloff;
            set => _heightFalloff = Clamp(value, 0.001f, 10.0f);
        }

        public float HeightDensity
        {
            get => _heightDensity;
            set => _heightDensity = Clamp(value, 0.0f, 1.0f);
        }

        public float MaxOpacity
        {
            get => _maxOpacity;
            set => _maxOpacity = Clamp(value, 0.0f, 1.0f);
        }

        public bool DirectionalInscatteringEnabled { get; set; } = true;
        public Vector3 DirectionalInscatteringColor { get; set; } = new(1.0f, 0.88f, 0.68f);

        /// <summary>
        /// Optional world-space light travel direction. Leave zero to use the first scene directional light.
        /// </summary>
        public Vector3 DirectionalInscatteringDirection { get; set; } = Vector3.Zero;

        public float DirectionalInscatteringIntensity
        {
            get => _directionalInscatteringIntensity;
            set => _directionalInscatteringIntensity = Clamp(value, 0.0f, 8.0f);
        }

        public float DirectionalInscatteringExponent
        {
            get => _directionalInscatteringExponent;
            set => _directionalInscatteringExponent = Clamp(value, 1.0f, 128.0f);
        }

        public FogDebugView DebugView { get; set; } = FogDebugView.None;
        public VolumetricFogSettings Volumetric { get; } = new();

        private static float Clamp(float value, float min, float max)
        {
            if (value < min)
                return min;
            return value > max ? max : value;
        }
    }

    public sealed class VolumetricFogSettings
    {
        private float _maxDistance = 250f;
        private float _baseExtinctionPerMeter = 0.015f;
        private float _heightExtinctionPerMeter = 0.04f;
        private float _height;
        private float _heightFalloff = 0.12f;
        private Vector3 _scatteringAlbedo = new(0.9f, 0.92f, 0.95f);
        private float _anisotropy = 0.2f;
        private Vector3 _globalWind;
        private float _noiseScale = 0.035f;
        private float _noiseStrength = 0.15f;
        private float _noiseContrast = 1f;
        private float _selfShadowDistance = 64f;
        private float _temporalHistoryWeight = 0.9f;
        private int _multipleScatteringIterations;
        private float _multipleScatteringEnergyLimit = 0.5f;
        private int _debugSlice = -1;

        /// <summary>
        /// Renderer-owned qualification for the production single-scattering
        /// path. Scene content cannot promote itself into this state.
        /// </summary>
        public bool SingleScatteringQualified { get; internal set; }

        /// <summary>
        /// Renderer-owned qualification for the bounded multiple-scattering
        /// extension. It is intentionally independent from single scattering.
        /// </summary>
        public bool MultipleScatteringQualified { get; internal set; }

        [Obsolete("Use SingleScatteringQualified. Qualification is renderer-owned.")]
        public bool ProfileQualified
        {
            get => SingleScatteringQualified;
            internal set => SingleScatteringQualified = value;
        }
        public float MaxDistance { get => _maxDistance; set => _maxDistance = Clamp(value, 0.1f, 10000f); }
        public float BaseExtinctionPerMeter { get => _baseExtinctionPerMeter; set => _baseExtinctionPerMeter = Clamp(value, 0f, 64f); }
        public float HeightExtinctionPerMeter { get => _heightExtinctionPerMeter; set => _heightExtinctionPerMeter = Clamp(value, 0f, 64f); }
        public float Height { get => _height; set => _height = FiniteOr(value, 0f); }
        public float HeightFalloff { get => _heightFalloff; set => _heightFalloff = Clamp(value, 0.001f, 10f); }
        public Vector3 ScatteringAlbedo { get => _scatteringAlbedo; set => _scatteringAlbedo = Clamp01(value); }
        public float Anisotropy { get => _anisotropy; set => _anisotropy = Clamp(value, -0.9f, 0.9f); }
        public Vector3 GlobalWind { get => _globalWind; set => _globalWind = FiniteOrZero(value); }
        public float NoiseScale { get => _noiseScale; set => _noiseScale = Clamp(value, 0.0001f, 1000f); }
        public float NoiseStrength { get => _noiseStrength; set => _noiseStrength = Clamp(value, 0f, 1f); }
        public float NoiseContrast { get => _noiseContrast; set => _noiseContrast = Clamp(value, 0.01f, 8f); }
        public float SelfShadowDistance { get => _selfShadowDistance; set => _selfShadowDistance = Clamp(value, 1f, 1000f); }
        public float TemporalHistoryWeight { get => _temporalHistoryWeight; set => _temporalHistoryWeight = Clamp(value, 0f, 0.95f); }
        public int MultipleScatteringIterations { get => _multipleScatteringIterations; set => _multipleScatteringIterations = Math.Clamp(value, 0, 2); }
        public float MultipleScatteringEnergyLimit { get => _multipleScatteringEnergyLimit; set => _multipleScatteringEnergyLimit = Clamp(value, 0f, 0.5f); }
        public int DebugSlice { get => _debugSlice; set => _debugSlice = Math.Clamp(value, -1, 95); }
        public FogDebugProjection DebugProjection { get; set; } =
            FogDebugProjection.MaxAlongRay;

        private static Vector3 Clamp01(Vector3 value) => new(
            Clamp(value.X, 0f, 1f),
            Clamp(value.Y, 0f, 1f),
            Clamp(value.Z, 0f, 1f));

        private static Vector3 FiniteOrZero(Vector3 value) => new(
            FiniteOr(value.X, 0f),
            FiniteOr(value.Y, 0f),
            FiniteOr(value.Z, 0f));

        private static float FiniteOr(float value, float fallback) =>
            float.IsFinite(value) ? value : fallback;

        private static float Clamp(float value, float minimum, float maximum)
        {
            if (!float.IsFinite(value))
                return minimum;
            return Math.Clamp(value, minimum, maximum);
        }
    }

    public sealed class RenderDiagnosticsSettings
    {
        public bool GpuMeshletCountersEnabled { get; set; }
        public bool DdgiForwardEstimateCountersEnabled { get; set; }
        public bool DirectionalShadowReceiverCountersEnabled { get; set; }

        /// <summary>
        /// Capture-only control for the disabled half of a paired forward-GI
        /// timing run. It suppresses receiver evaluation without disabling DDGI
        /// production, paging, transport, or reflection-capture state. Normal
        /// rendering and every quality preset leave this false.
        /// </summary>
        public bool SuppressForwardGiGatherForBenchmark { get; set; }

        /// <summary>
        /// Capture-only performance switch that forces the approximate
        /// screen-space DDGI receiver cache even when the active quality tier
        /// normally requires the exact per-fragment gather. Normal rendering
        /// and every quality preset leave this false.
        /// </summary>
        public bool ForceForwardGiReceiverCacheForBenchmark { get; set; }

        /// <summary>
        /// Capture-only quality oracle switch. It keeps DDGI enabled but
        /// bypasses the screen-space receiver cache so a settled frame can be
        /// compared against the exact per-fragment gather.
        /// Normal rendering and every quality preset leave this false.
        /// </summary>
        public bool ForceExactForwardGiGatherForBenchmark { get; set; }
    }

    public sealed class HiZOcclusionSettings
    {
        private int _previousFrameUvPaddingPixels = 8;
        private float _occlusionBias = 0.0005f;
        private float _fastCameraMotionDistanceThreshold = 1.0f;
        private float _fastCameraMotionForwardDotThreshold = 0.985f;
        private int _cameraMotionSuppressionFrames = 1;

        public bool SceneSubmissionPreviousFrameCullingEnabled { get; set; } = true;
        public bool CurrentFrameForwardVisibilityCompactionEnabled { get; set; } = true;

        public int PreviousFrameUvPaddingPixels
        {
            get => _previousFrameUvPaddingPixels;
            set => _previousFrameUvPaddingPixels = Math.Clamp(value, 0, 64);
        }

        public float OcclusionBias
        {
            get => _occlusionBias;
            set => _occlusionBias = float.IsFinite(value) ? Math.Clamp(value, 0.0f, 0.1f) : 0.0005f;
        }

        public bool DisablePreviousFrameCullingDuringFastCameraMotion { get; set; } = true;

        public float FastCameraMotionDistanceThreshold
        {
            get => _fastCameraMotionDistanceThreshold;
            set => _fastCameraMotionDistanceThreshold = float.IsFinite(value) ? Math.Clamp(value, 0.0f, 100.0f) : 1.0f;
        }

        public float FastCameraMotionForwardDotThreshold
        {
            get => _fastCameraMotionForwardDotThreshold;
            set => _fastCameraMotionForwardDotThreshold = float.IsFinite(value) ? Math.Clamp(value, -1.0f, 1.0f) : 0.985f;
        }

        public int CameraMotionSuppressionFrames
        {
            get => _cameraMotionSuppressionFrames;
            set => _cameraMotionSuppressionFrames = Math.Clamp(value, 1, 8);
        }

        public bool Enabled { get; set; } = true;
        public bool AdaptiveEnabled { get; set; } = true;

        public bool PreviousFrameSceneSubmissionEnabled
        {
            get => SceneSubmissionPreviousFrameCullingEnabled;
            set => SceneSubmissionPreviousFrameCullingEnabled = value;
        }

        public bool CurrentFrameForwardVisibilityEnabled
        {
            get => CurrentFrameForwardVisibilityCompactionEnabled;
            set => CurrentFrameForwardVisibilityCompactionEnabled = value;
        }

        public bool ForceOn { get; set; }
        public bool ForceProbe { get; set; }
        public bool ValidateAgainstLegacyPath { get; set; }
    }

    public sealed class AsyncComputeSettings
    {
        /// <summary>
        /// Defaults to <see cref="AsyncComputeMode.Auto"/> for new installations. Auto starts on
        /// the graphics queue, collects isolated timing samples, and only promotes a complete
        /// resource plan after it proves a sustained benefit. Pre-v3 files without a Mode keep
        /// their legacy graphics-only behavior during settings migration.
        /// </summary>
        public AsyncComputeMode Mode { get; set; } = AsyncComputeMode.Auto;

        /// <summary>Atomic path explicitly authorized by a validation harness in Force mode.</summary>
        public AsyncComputePath? ForceValidationPath { get; set; }

        /// <summary>
        /// Compatibility shim for settings written before <see cref="Mode"/> existed.  Setting this
        /// to true is intentionally a validation request rather than a production auto-enable: an
        /// old explicit opt-in must never silently become a profitability decision.
        /// </summary>
        [Obsolete("Use Mode. Legacy true maps to ForceEnabledForValidation.")]
        public bool Enabled
        {
            get => Mode != AsyncComputeMode.Disabled;
            set => Mode = value
                ? AsyncComputeMode.ForceEnabledForValidation
                : AsyncComputeMode.Disabled;
        }

        public bool HiZBuildEnabled { get; set; } = true;
        public bool AmbientOcclusionBlurEnabled { get; set; } = true;
        public bool FogEnabled { get; set; } = true;
        public bool BloomEnabled { get; set; } = true;
        public bool SimpleDdgiUpdateEnabled { get; set; } = true;
        public bool FarFieldClipmapBakeEnabled { get; set; } = true;
        public bool GpuParticlesEnabled { get; set; } = true;

        /// <summary>Minimum samples retained for each graphics-only/async timing window.</summary>
        public int AutoMinimumSampleCount { get; set; } = 30;

        /// <summary>Minimum warm-up frames before Auto may promote a path.</summary>
        public int AutoWarmupFrameCount { get; set; } = 60;

        /// <summary>Absolute GPU frame-time benefit required by Auto, in milliseconds.</summary>
        public float AutoMinimumAbsoluteBenefitMilliseconds { get; set; } = 0.25f;

        /// <summary>Relative GPU frame-time benefit required by Auto.</summary>
        public float AutoMinimumRelativeBenefit { get; set; } = 0.03f;

        /// <summary>Frames a path remains in its current decision before Auto may flip it again.</summary>
        public int AutoDecisionCooldownFrames { get; set; } = 180;
    }

    public sealed class RenderSettings
    {
        /// <summary>Current durable settings-file schema used by capture metadata and persistence.</summary>
        public const int SerializationVersion = 23;
        internal const int MaximumSettingsFileBytes = 4 * 1024 * 1024;

        private float _exposure = 1.0f;
        private float _resolutionScale = 1.0f;

        /// <summary>
        /// Internal pre-publication guard used by renderer-owned tier budgets.
        /// Subscribers run before any preset state mutates and may reject a
        /// transition by throwing, leaving the previous preset intact.
        /// </summary>
        internal event Action<RenderQualityPreset>? QualityPresetChanging;

        public float Exposure
        {
            get => _exposure;
            set => _exposure = value < 0.0f ? 0.0f : value;
        }

        public float ResolutionScale
        {
            get => _resolutionScale;
            set => _resolutionScale = ClampScale(value);
        }

        public float EffectiveResolutionScale => DynamicResolution.ClampResolvedScale(_resolutionScale);

        public ToneMapper ToneMapper { get; set; } = ToneMapper.AcesFitted;
        public bool ShowRawHdrSceneColor { get; set; }
        public DynamicResolutionSettings DynamicResolution { get; } = new();
        public AutoExposureSettings AutoExposure { get; } = new();
        public ShadowSettings Shadows { get; } = new();
        public BloomSettings Bloom { get; } = new();
        public EnvironmentSettings Environment { get; } = new();
        public ReflectionSettings Reflections { get; } = new();
        public AmbientOcclusionSettings AmbientOcclusion { get; } = new();
        public GlobalIlluminationSettings GlobalIllumination { get; } = new();
        public AntiAliasingSettings AntiAliasing { get; } = new();
        public FogSettings Fog { get; } = new();
        public TransparencySettings Transparency { get; } = new();
        public DecalSettings Decals { get; } = new();
        public AnimationSettings Animation { get; } = new();
        public ParticleSettings Particles { get; } = new();
        public FoliageSettings Foliage { get; } = new();
        public SceneSubmissionSettings SceneSubmission { get; } = new();
        public MaterialSettings Materials { get; } = new();
        public RasterSettings Raster { get; } = new();
        public RenderDiagnosticsSettings Diagnostics { get; } = new();
        public HiZVisibilityPolicySettings HiZVisibilityPolicy { get; } = new();
        public HiZOcclusionSettings HiZOcclusion { get; } = new();
        public AsyncComputeSettings AsyncCompute { get; } = new();
        public DebugOverlaySettings Debug { get; } = new();
        public RenderBudgetSettings PerformanceBudgets { get; } = new();
        public RenderQualityPreset QualityPreset { get; private set; } = RenderQualityPreset.DdgiHigh;
        public RenderFeatureIsolationMode FeatureIsolation { get; set; } = RenderFeatureIsolationMode.FullFrame;
        public HiZTestMode HiZTestMode { get; set; } = HiZTestMode.Bounds4Tap;
        public bool UseSecondaryCommandBuffers { get; set; } = true;
        public bool UseCameraDependentCpuScenePayload { get; set; } = true;
        public bool UseCpuMeshletFrustumCulling { get; set; } = true;
        /// <summary>
        /// Enables conservative one-sided solid meshlet backface rejection.
        /// Invalid or legacy cone records remain visible and therefore fail
        /// closed without disabling the preset-selected path.
        /// </summary>
        public bool MeshletNormalConeCullingEnabled { get; set; }

        public RenderSettings()
        {
            ApplyQualityPreset(RenderQualityPreset.DdgiHigh);
        }

        /// <summary>
        /// Clears diagnostic visualization overrides without changing the selected
        /// quality profile or its physical rendering settings.
        /// </summary>
        public void ResetRenderViewOverrides()
        {
            ShowRawHdrSceneColor = false;
            FeatureIsolation = RenderFeatureIsolationMode.FullFrame;
            Shadows.DebugView = ShadowDebugView.None;
            Shadows.DirectionalShadowPreviewCascade = 0;
            Shadows.ForceStaticCascadeCacheRefresh = false;
            Bloom.DebugView = BloomDebugView.None;
            Environment.DebugView = EnvironmentDebugView.None;
            Reflections.DebugView = ReflectionDebugView.None;
            AmbientOcclusion.DebugView = AmbientOcclusionDebugView.None;
            GlobalIllumination.DebugView = GlobalIlluminationDebugView.None;
            AntiAliasing.DebugView = AntiAliasingDebugView.None;
            Fog.DebugView = FogDebugView.None;
            Transparency.DebugView = TransparencyDebugView.None;
            Decals.DebugView = DecalDebugView.None;
            Animation.DebugView = AnimationDebugView.None;
            Particles.DebugView = ParticleDebugView.None;
            Foliage.DebugView = FoliageDebugView.None;
            Materials.DebugView = MaterialDebugView.None;
            Debug.Mode = DebugOverlayMode.None;
        }

        public void ApplyQualityPreset(RenderQualityPreset preset)
        {
            if (!Enum.IsDefined(preset))
                throw new ArgumentOutOfRangeException(nameof(preset));

            QualityPresetChanging?.Invoke(preset);
            QualityPreset = preset;
            ShowRawHdrSceneColor = false;

            // Qualification belongs to the renderer profile, never to scene
            // configuration. High-class profiles ship the qualified
            // single-scattering path; only Ultra admits the separately bounded
            // multiple-scattering extension.
            Fog.Volumetric.SingleScatteringQualified = preset is
                RenderQualityPreset.High or
                RenderQualityPreset.DdgiHigh or
                RenderQualityPreset.Ultra;
            Fog.Volumetric.MultipleScatteringQualified =
                preset == RenderQualityPreset.Ultra;
            Fog.Volumetric.MultipleScatteringIterations =
                preset == RenderQualityPreset.Ultra ? 2 : 0;

            // Quality presets select the bounded C3 production path. Its
            // publication handshake is transactional and preserves the
            // canonical uniform DDGI proposal whenever guiding is unavailable.
            // Callers can still opt out after applying a preset by selecting
            // Off, and persisted settings retain that explicit intent.
            GlobalIllumination.SimpleDdgiDirectionalGuidingMode =
                GlobalIlluminationSettings
                    .DefaultSimpleDdgiDirectionalGuidingMode;

            // C5 is part of the High-class production profile. Lower tiers do
            // not reserve its immutable trace, history, filter, and composite
            // resources. Applying a preset deliberately restores the tier
            // default; persisted settings and explicit runtime/CLI overrides
            // are applied after the preset and may still opt out.
            GlobalIllumination.SimpleDdgiNearFieldResidualMode = preset is
                RenderQualityPreset.High or
                RenderQualityPreset.DdgiHigh or
                RenderQualityPreset.Ultra
                    ? GlobalIlluminationSettings
                        .DefaultSimpleDdgiNearFieldResidualMode
                    : SimpleDdgiNearFieldResidualMode.Off;

            // Bent-normal irradiance is normal-dependent and remains paired
            // with the exact receiver gather. The cache candidates stay
            // available through explicit settings and benchmark variants, but
            // supplied hardware captures did not pass their visual gate.
            AmbientOcclusion.Mode = preset == RenderQualityPreset.Low
                ? AmbientOcclusionMode.Disabled
                : AmbientOcclusionMode.Gtao;
            AmbientOcclusion.BentNormalMode = preset switch
            {
                RenderQualityPreset.High =>
                    AmbientOcclusionBentNormalMode.EnvironmentOnly,
                RenderQualityPreset.Ultra =>
                    AmbientOcclusionBentNormalMode.EnvironmentAndDdgi,
                _ => AmbientOcclusionBentNormalMode.Off
            };
            GlobalIllumination.SimpleDdgiReceiverCacheMode =
                SimpleDdgiReceiverCacheMode.Exact;
            GlobalIllumination
                .SimpleDdgiNearFieldResidualLocalAdaptiveSchedulingEnabled =
                preset is RenderQualityPreset.High or
                    RenderQualityPreset.DdgiHigh or
                    RenderQualityPreset.Ultra;
            MeshletNormalConeCullingEnabled = true;
            Transparency.PipelinePartitioningEnabled = true;
            SceneSubmission.GpuLodSelectionMode =
                GpuLodSelectionMode.ScreenSpaceError;
            SceneSubmission.GpuLodTargetPixelError = preset switch
            {
                RenderQualityPreset.Low => 2.0f,
                RenderQualityPreset.Medium => 1.5f,
                RenderQualityPreset.Ultra => 0.5f,
                _ => 1.0f
            };
            Materials.SpecularAntialiasingMode = preset ==
                RenderQualityPreset.Low
                    ? SpecularAntialiasingMode.Off
                    : SpecularAntialiasingMode.GeometricVariance;
            Raster.VariableRateShadingMode = preset is
                RenderQualityPreset.Low or
                RenderQualityPreset.Medium or
                RenderQualityPreset.DdgiHigh
                    ? VariableRateShadingMode.Auto
                    : VariableRateShadingMode.Off;

            AmbientOcclusion.GtaoQualityPreset = preset switch
            {
                RenderQualityPreset.Low => GtaoQualityPreset.Low,
                RenderQualityPreset.Medium => GtaoQualityPreset.Low,
                RenderQualityPreset.High => GtaoQualityPreset.Balanced,
                RenderQualityPreset.DdgiHigh => GtaoQualityPreset.High,
                RenderQualityPreset.Ultra => GtaoQualityPreset.High,
                _ => GtaoQualityPreset.Balanced
            };

            switch (preset)
            {
                case RenderQualityPreset.Low:
                    ResolutionScale = 0.75f;
                    DynamicResolution.Enabled = true;
                    DynamicResolution.MinimumScale = 0.5f;
                    DynamicResolution.MaximumScale = 0.85f;
                    Bloom.Enabled = false;
                    Fog.Enabled = false;
                    AmbientOcclusion.Enabled = false;
                    GlobalIllumination.Enabled = false;
                    GlobalIllumination.Mode = GlobalIlluminationMode.Disabled;
                    GlobalIllumination.DebugView = GlobalIlluminationDebugView.None;
                    GlobalIllumination.IndirectIntensity = 0.0f;
                    GlobalIllumination.EnvironmentFallbackIntensity = 1.0f;
                    GlobalIllumination.UseDdgi = false;
                    GlobalIllumination.UseRayQueryBackend = false;
                    GlobalIllumination.ApplyDdgiQualityTier(DdgiQualityTier.DdgiLow);
                    GlobalIllumination.DdgiProbeClassificationEnabled = true;
                    GlobalIllumination.DdgiProbeRelocationEnabled = false;
                    GlobalIllumination.DdgiCameraRelativeEnabled = false;
                    GlobalIllumination.DdgiAsyncComputeEnabled = false;
                    GlobalIllumination.DdgiProbeUpdatePrimaryRayBudget = GlobalIlluminationSettings.DefaultDdgiProbeUpdatePrimaryRayBudget;
                    GlobalIllumination.ResolutionScale = 0.5f;
                    GlobalIllumination.MaxBounceDistance = 3.0f;
                    Reflections.Enabled = true;
                    Reflections.Mode = ReflectionMode.StaticProbes;
                    Particles.Enabled = true;
                    Foliage.Enabled = true;
                    Foliage.GpuDrivenEnabled = true;
                    Foliage.HiZCullingEnabled = true;
                    Foliage.CastShadows = false;
                    Foliage.DensityScale = 0.45f;
                    Foliage.MaxDrawDistance = 90f;
                    Foliage.GrassShadowDistance = 0f;
                    Foliage.GrassShadowDensityScale = 0f;
                    Foliage.LocalShadowsEnabled = false;
                    Foliage.MaxLocalShadowedSpotLights = 0;
                    Foliage.MaxLocalShadowedPointLights = 0;
                    Foliage.MaxLocalShadowClusters = 0;
                    Foliage.MaxLocalShadowMeshletDraws = 0;
                    Foliage.MaxVisibleClusters = 65536;
                    Foliage.MaxVisibleMeshletDraws = 131072;
                    AntiAliasing.Mode = AntiAliasingMode.Fxaa;
                    Shadows.DirectionalCascadeCount = 1;
                    Shadows.DirectionalFilterMode =
                        DirectionalShadowFilterMode.LegacyBoxPcf;
                    Shadows.DirectionalBiasMode =
                        DirectionalShadowBiasMode.Legacy;
                    Shadows.DirectionalPcfRadiusMode =
                        DirectionalPcfRadiusMode.Constant;
                    Shadows.SpotShadowsEnabled = false;
                    Shadows.MaxShadowedSpotLights = 0;
                    Shadows.PointShadowsEnabled = false;
                    Shadows.MaxShadowedPointLights = 0;
                    Shadows.AreaShadowsEnabled = false;
                    Shadows.MaxShadowedAreaLights = 0;
                    Shadows.AreaShadowSampleCount = 1;
                    Transparency.Mode = TransparencyMode.SortedAlphaBlend;
                    Transparency.ReceiveGlobalIllumination = false;
                    Transparency.SampleReflections = false;
                    Transparency.SceneReflectionRayTaskBudget = 0;
                    Transparency.SceneReflectionSsrSampleBudget = 0;
                    Decals.ReceiveGlobalIllumination = false;
                    break;
                case RenderQualityPreset.Medium:
                    ResolutionScale = 0.9f;
                    DynamicResolution.Enabled = true;
                    DynamicResolution.MinimumScale = 0.65f;
                    DynamicResolution.MaximumScale = 1.0f;
                    Bloom.Enabled = true;
                    Bloom.MipCount = 5;
                    Fog.Enabled = false;
                    AmbientOcclusion.Enabled = true;
                    AmbientOcclusion.ResolutionScale = 0.5f;
                    AmbientOcclusion.SampleCount = 8;
                    GlobalIllumination.Enabled = true;
                    GlobalIllumination.Mode = GlobalIlluminationMode.Ddgi;
                    GlobalIllumination.DebugView = GlobalIlluminationDebugView.None;
                    GlobalIllumination.IndirectIntensity = 0.75f;
                    GlobalIllumination.EnvironmentFallbackIntensity = 1.0f;
                    GlobalIllumination.UseDdgi = true;
                    GlobalIllumination.UseRayQueryBackend = true;
                    GlobalIllumination.ApplyDdgiQualityTier(DdgiQualityTier.DdgiMedium);
                    GlobalIllumination.DdgiProbeClassificationEnabled = true;
                    GlobalIllumination.DdgiProbeRelocationEnabled = false;
                    GlobalIllumination.DdgiCameraRelativeEnabled = false;
                    GlobalIllumination.DdgiAsyncComputeEnabled = false;
                    GlobalIllumination.DdgiProbeUpdatePrimaryRayBudget = GlobalIlluminationSettings.DefaultDdgiProbeUpdatePrimaryRayBudget;
                    GlobalIllumination.ResolutionScale = 0.5f;
                    GlobalIllumination.MaxBounceDistance = 4.0f;
                    GlobalIllumination.TemporalEnabled = true;
                    GlobalIllumination.DenoiserEnabled = true;
                    Reflections.Enabled = true;
                    Reflections.MaxProbesPerPixel = 1;
                    Reflections.ApplyHybridQualityBudget(RenderQualityPreset.Medium);
                    Particles.Enabled = true;
                    Foliage.Enabled = true;
                    Foliage.GpuDrivenEnabled = true;
                    Foliage.HiZCullingEnabled = true;
                    Foliage.CastShadows = true;
                    Foliage.DensityScale = 0.75f;
                    Foliage.MaxDrawDistance = 160f;
                    Foliage.GrassShadowDistance = 15f;
                    Foliage.GrassShadowDensityScale = 0.35f;
                    Foliage.LocalShadowsEnabled = false;
                    Foliage.MaxLocalShadowedSpotLights = 1;
                    Foliage.MaxLocalShadowedPointLights = 0;
                    Foliage.MaxLocalShadowClusters = 2048;
                    Foliage.MaxLocalShadowMeshletDraws = 4096;
                    Foliage.MaxVisibleClusters = 131072;
                    Foliage.MaxVisibleMeshletDraws = 262144;
                    AntiAliasing.Mode = AntiAliasingMode.SmaaMedium;
                    Shadows.DirectionalCascadeCount = 2;
                    Shadows.DirectionalFilterMode =
                        DirectionalShadowFilterMode.TentPcf;
                    Shadows.DirectionalBiasMode =
                        DirectionalShadowBiasMode.WorldTexelScaled;
                    Shadows.DirectionalPcfRadiusMode =
                        DirectionalPcfRadiusMode.WorldSpaceAdaptive;
                    Shadows.SpotShadowsEnabled = true;
                    Shadows.PointShadowsEnabled = true;
                    Shadows.MaxShadowedSpotLights = 2;
                    Shadows.MaxShadowedPointLights = 1;
                    Shadows.AreaShadowsEnabled = true;
                    Shadows.MaxShadowedAreaLights = 1;
                    Shadows.AreaShadowSampleCount = 1;
                    Transparency.Mode = TransparencyMode.SortedAlphaBlend;
                    // Dynamic diffuse GI is intentionally not sampled by
                    // layered forward receivers in this quality tier.
                    Transparency.ReceiveGlobalIllumination = false;
                    Transparency.SampleReflections = false;
                    Transparency.SceneReflectionRayTaskBudget = 0;
                    Transparency.SceneReflectionSsrSampleBudget = 0;
                    Decals.ReceiveGlobalIllumination = false;
                    break;
                case RenderQualityPreset.DdgiHigh:
                    ResolutionScale = 1.0f;
                    DynamicResolution.Enabled = false;
                    Bloom.Enabled = true;
                    Bloom.MipCount = 6;
                    Fog.Enabled = false;
                    AmbientOcclusion.Enabled = true;
                    AmbientOcclusion.ResolutionScale = 1.0f;
                    AmbientOcclusion.SampleCount = 32;
                    GlobalIllumination.Enabled = true;
                    GlobalIllumination.Mode = GlobalIlluminationMode.Ddgi;
                    GlobalIllumination.DebugView = GlobalIlluminationDebugView.None;
                    GlobalIllumination.IndirectIntensity = 1.0f;
                    GlobalIllumination.EnvironmentFallbackIntensity = 1.0f;
                    GlobalIllumination.UseDdgi = true;
                    GlobalIllumination.UseRayQueryBackend = true;
                    GlobalIllumination.DdgiProbeClassificationEnabled = true;
                    GlobalIllumination.DdgiProbeRelocationEnabled = true;
                    GlobalIllumination.DdgiCameraRelativeEnabled = true;
                    GlobalIllumination.ApplyDdgiQualityTier(DdgiQualityTier.DdgiHigh);
                    GlobalIllumination.DdgiAsyncComputeEnabled = true;
                    GlobalIllumination.ResolutionScale = 0.5f;
                    GlobalIllumination.MaxBounceDistance = 10.0f;
                    GlobalIllumination.TemporalEnabled = false;
                    GlobalIllumination.DenoiserEnabled = false;
                    Reflections.Enabled = true;
                    Reflections.MaxProbesPerPixel = 2;
                    Reflections.ApplyHybridQualityBudget(RenderQualityPreset.DdgiHigh);
                    Particles.Enabled = true;
                    Foliage.Enabled = true;
                    Foliage.GpuDrivenEnabled = true;
                    Foliage.HiZCullingEnabled = true;
                    Foliage.CastShadows = true;
                    Foliage.DensityScale = 1.0f;
                    Foliage.MaxDrawDistance = 250f;
                    Foliage.GrassShadowDistance = 25f;
                    Foliage.GrassShadowDensityScale = 0.5f;
                    Foliage.LocalShadowsEnabled = true;
                    Foliage.MaxLocalShadowedSpotLights = 1;
                    Foliage.MaxLocalShadowedPointLights = 1;
                    Foliage.MaxLocalShadowClusters = 4096;
                    Foliage.MaxLocalShadowMeshletDraws = 8192;
                    Foliage.MaxVisibleClusters = 262144;
                    Foliage.MaxVisibleMeshletDraws = 524288;
                    AntiAliasing.Mode = AntiAliasingMode.SmaaHigh;
                    Shadows.DirectionalCascadeCount = 3;
                    Shadows.DirectionalFilterMode =
                        DirectionalShadowFilterMode.TentPcf;
                    Shadows.DirectionalBiasMode =
                        DirectionalShadowBiasMode.WorldTexelScaled;
                    Shadows.DirectionalPcfRadiusMode =
                        DirectionalPcfRadiusMode.WorldSpaceAdaptive;
                    Shadows.SpotShadowsEnabled = true;
                    Shadows.PointShadowsEnabled = true;
                    Shadows.MaxShadowedSpotLights = Math.Max(Shadows.MaxShadowedSpotLights, 3);
                    Shadows.MaxShadowedPointLights = Math.Max(Shadows.MaxShadowedPointLights, 1);
                    Shadows.AreaShadowsEnabled = true;
                    Shadows.MaxShadowedAreaLights = Math.Max(
                        Shadows.MaxShadowedAreaLights,
                        2);
                    Shadows.AreaShadowSampleCount = 1;
                    Transparency.Mode = TransparencyMode.SortedAlphaBlend;
                    Transparency.ReceiveGlobalIllumination = true;
                    Transparency.SampleReflections = true;
                    Transparency.SceneReflectionRayTaskBudget = 65_536;
                    Transparency.SceneReflectionSsrSampleBudget = 4_194_304;
                    Decals.ReceiveGlobalIllumination = true;
                    break;
                case RenderQualityPreset.Ultra:
                    ResolutionScale = 1.0f;
                    DynamicResolution.Enabled = false;
                    Bloom.Enabled = true;
                    Bloom.MipCount = 8;
                    Fog.Enabled = false;
                    AmbientOcclusion.Enabled = true;
                    AmbientOcclusion.ResolutionScale = 1.0f;
                    AmbientOcclusion.SampleCount = 32;
                    GlobalIllumination.Enabled = true;
                    GlobalIllumination.Mode = GlobalIlluminationMode.Ddgi;
                    GlobalIllumination.DebugView = GlobalIlluminationDebugView.None;
                    GlobalIllumination.IndirectIntensity = 1.0f;
                    GlobalIllumination.EnvironmentFallbackIntensity = 1.0f;
                    GlobalIllumination.UseDdgi = true;
                    GlobalIllumination.UseRayQueryBackend = true;
                    GlobalIllumination.DdgiProbeClassificationEnabled = true;
                    GlobalIllumination.DdgiProbeRelocationEnabled = true;
                    GlobalIllumination.DdgiCameraRelativeEnabled = true;
                    GlobalIllumination.DdgiAsyncComputeEnabled = true;
                    GlobalIllumination.ApplyDdgiQualityTier(DdgiQualityTier.DdgiUltra);
                    GlobalIllumination.ResolutionScale = 0.5f;
                    GlobalIllumination.MaxBounceDistance = 10.0f;
                    GlobalIllumination.TemporalEnabled = true;
                    GlobalIllumination.DenoiserEnabled = true;
                    Reflections.Enabled = true;
                    Reflections.MaxProbesPerPixel = ReflectionSettings.ShaderMaxProbesPerPixel;
                    Reflections.ApplyHybridQualityBudget(RenderQualityPreset.Ultra);
                    Particles.Enabled = true;
                    Foliage.Enabled = true;
                    Foliage.GpuDrivenEnabled = true;
                    Foliage.HiZCullingEnabled = true;
                    Foliage.CastShadows = true;
                    Foliage.DensityScale = 1.5f;
                    Foliage.MaxDrawDistance = 400f;
                    Foliage.GrassShadowDistance = 45f;
                    Foliage.GrassShadowDensityScale = 0.75f;
                    Foliage.LocalShadowsEnabled = true;
                    Foliage.MaxLocalShadowedSpotLights = 2;
                    Foliage.MaxLocalShadowedPointLights = 1;
                    Foliage.MaxLocalShadowClusters = 8192;
                    Foliage.MaxLocalShadowMeshletDraws = 16384;
                    Foliage.MaxVisibleClusters = 524288;
                    Foliage.MaxVisibleMeshletDraws = 1048576;
                    AntiAliasing.Mode = AntiAliasingMode.SmaaHigh;
                    Shadows.DirectionalCascadeCount = ShadowSettings.MaxDirectionalCascades;
                    Shadows.DirectionalFilterMode =
                        DirectionalShadowFilterMode.TentPcf;
                    Shadows.DirectionalBiasMode =
                        DirectionalShadowBiasMode.WorldTexelScaled;
                    Shadows.DirectionalPcfRadiusMode =
                        DirectionalPcfRadiusMode.WorldSpaceAdaptive;
                    Shadows.SpotShadowsEnabled = true;
                    Shadows.PointShadowsEnabled = true;
                    Shadows.MaxShadowedSpotLights = Math.Max(Shadows.MaxShadowedSpotLights, 4);
                    Shadows.MaxShadowedPointLights = Math.Max(Shadows.MaxShadowedPointLights, 1);
                    Shadows.AreaShadowsEnabled = true;
                    Shadows.MaxShadowedAreaLights = 4;
                    Shadows.AreaShadowSampleCount = 2;
                    Transparency.Mode = TransparencyMode.SortedAlphaBlend;
                    Transparency.ReceiveGlobalIllumination = true;
                    Transparency.SampleReflections = true;
                    Transparency.SceneReflectionRayTaskBudget = 131_072;
                    Transparency.SceneReflectionSsrSampleBudget = 8_388_608;
                    Decals.ReceiveGlobalIllumination = true;
                    break;
                case RenderQualityPreset.High:
                    ResolutionScale = 1.0f;
                    DynamicResolution.Enabled = false;
                    Bloom.Enabled = true;
                    Bloom.MipCount = 6;
                    Fog.Enabled = false;
                    AmbientOcclusion.Enabled = true;
                    AmbientOcclusion.ResolutionScale = 0.5f;
                    AmbientOcclusion.SampleCount = 16;
                    GlobalIllumination.Enabled = true;
                    GlobalIllumination.Mode = GlobalIlluminationMode.Ddgi;
                    GlobalIllumination.DebugView = GlobalIlluminationDebugView.None;
                    GlobalIllumination.IndirectIntensity = 1.0f;
                    GlobalIllumination.EnvironmentFallbackIntensity = 1.0f;
                    GlobalIllumination.UseDdgi = true;
                    // High enables the production DDGI path. Leaving its only
                    // shipping trace backend disabled produces an internally
                    // contradictory profile: the gather and SSGI residual run,
                    // but no probe can ever acquire irradiance or visibility.
                    GlobalIllumination.UseRayQueryBackend = true;
                    GlobalIllumination.ApplyDdgiQualityTier(DdgiQualityTier.DdgiHigh);
                    GlobalIllumination.DdgiProbeClassificationEnabled = true;
                    GlobalIllumination.DdgiProbeRelocationEnabled = true;
                    GlobalIllumination.DdgiCameraRelativeEnabled = true;
                    GlobalIllumination.DdgiAsyncComputeEnabled = true;
                    GlobalIllumination.DdgiProbeUpdatePrimaryRayBudget = GlobalIlluminationSettings.DefaultDdgiProbeUpdatePrimaryRayBudget;
                    GlobalIllumination.ResolutionScale = 0.5f;
                    GlobalIllumination.MaxBounceDistance = 6.0f;
                    GlobalIllumination.TemporalEnabled = true;
                    GlobalIllumination.DenoiserEnabled = true;
                    Reflections.Enabled = true;
                    Reflections.MaxProbesPerPixel = 2;
                    Reflections.ApplyHybridQualityBudget(RenderQualityPreset.High);
                    Particles.Enabled = true;
                    Foliage.Enabled = true;
                    Foliage.GpuDrivenEnabled = true;
                    Foliage.HiZCullingEnabled = true;
                    Foliage.CastShadows = true;
                    Foliage.DensityScale = 1.0f;
                    Foliage.MaxDrawDistance = 250f;
                    Foliage.GrassShadowDistance = 25f;
                    Foliage.GrassShadowDensityScale = 0.5f;
                    Foliage.LocalShadowsEnabled = true;
                    Foliage.MaxLocalShadowedSpotLights = 1;
                    Foliage.MaxLocalShadowedPointLights = 1;
                    Foliage.MaxLocalShadowClusters = 4096;
                    Foliage.MaxLocalShadowMeshletDraws = 8192;
                    Foliage.MaxVisibleClusters = 262144;
                    Foliage.MaxVisibleMeshletDraws = 524288;
                    AntiAliasing.Mode = AntiAliasingMode.SmaaMedium;
                    Shadows.DirectionalCascadeCount = 2;
                    Shadows.DirectionalFilterMode =
                        DirectionalShadowFilterMode.TentPcf;
                    Shadows.DirectionalBiasMode =
                        DirectionalShadowBiasMode.WorldTexelScaled;
                    Shadows.DirectionalPcfRadiusMode =
                        DirectionalPcfRadiusMode.WorldSpaceAdaptive;
                    Shadows.SpotShadowsEnabled = true;
                    Shadows.PointShadowsEnabled = true;
                    Shadows.MaxShadowedSpotLights = Math.Max(Shadows.MaxShadowedSpotLights, 2);
                    Shadows.MaxShadowedPointLights = Math.Max(Shadows.MaxShadowedPointLights, 1);
                    Shadows.AreaShadowsEnabled = true;
                    Shadows.MaxShadowedAreaLights = Math.Max(
                        Shadows.MaxShadowedAreaLights,
                        2);
                    Shadows.AreaShadowSampleCount = 1;
                    Transparency.Mode = TransparencyMode.SortedAlphaBlend;
                    Transparency.ReceiveGlobalIllumination = true;
                    Transparency.SampleReflections = true;
                    Transparency.SceneReflectionRayTaskBudget = 65_536;
                    Transparency.SceneReflectionSsrSampleBudget = 4_194_304;
                    Decals.ReceiveGlobalIllumination = true;
                    break;
            }
        }

        public void Save(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Render settings path cannot be null or empty.", nameof(path));

            byte[] payload = SerializePersistencePayload();

            string fullPath = Path.GetFullPath(path);
            string directory =
                Path.GetDirectoryName(fullPath)
                ?? throw new InvalidOperationException(
                    $"Render settings path '{fullPath}' has no parent directory.");
            Directory.CreateDirectory(directory);
            string temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var output = new FileStream(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           bufferSize: 64 * 1024,
                           options: FileOptions.WriteThrough))
                {
                    output.Write(payload);
                    output.Flush(flushToDisk: true);
                }

                if (File.Exists(fullPath))
                {
                    File.Replace(
                        temporaryPath,
                        fullPath,
                        destinationBackupFileName: null,
                        ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporaryPath, fullPath);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }

        /// <summary>
        /// Creates a detached, persistence-equivalent settings snapshot.
        /// Renderer-owned callbacks and mutable child objects are deliberately
        /// not shared with the source instance, making the result safe to edit
        /// as a cold-start transaction while the live renderer keeps running.
        /// </summary>
        public RenderSettings CreateSnapshot()
        {
            var snapshot = new RenderSettings();
            RenderSettingsFile.FromSettings(this).ApplyTo(snapshot);
            return snapshot;
        }

        /// <summary>
        /// Hashes the complete deterministic persisted document, including
        /// non-GI settings. Advanced-GI qualification uses its narrower GI
        /// fingerprint, while startup transaction integrity uses this hash so
        /// an older profile cannot observe a different settings snapshot.
        /// </summary>
        public string ComputePersistenceSha256()
        {
            byte[] payload = SerializePersistencePayload();
            return "sha256:" + Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(payload))
                .ToLowerInvariant();
        }

        public static RenderSettings Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Render settings path cannot be null or empty.", nameof(path));
            string fullPath = Path.GetFullPath(path);
            using var input = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                options: FileOptions.SequentialScan);
            long admittedLength = input.Length;
            if (admittedLength <= 0 ||
                admittedLength > MaximumSettingsFileBytes)
            {
                throw new InvalidDataException(
                    $"Render settings file '{fullPath}' contains {admittedLength} bytes; " +
                    $"the valid range is [1, {MaximumSettingsFileBytes}].");
            }

            var payload = new byte[checked((int)admittedLength)];
            try
            {
                input.ReadExactly(payload);
            }
            catch (EndOfStreamException exception)
            {
                throw new InvalidDataException(
                    $"Render settings file '{fullPath}' became shorter during its bounded read.",
                    exception);
            }

            if (input.ReadByte() != -1 ||
                input.Length != admittedLength)
            {
                throw new InvalidDataException(
                    $"Render settings file '{fullPath}' changed length during its bounded read.");
            }

            RenderSettingsFile? file =
                JsonSerializer.Deserialize<RenderSettingsFile>(
                    payload,
                    CreateJsonOptions());
            if (file == null)
            {
                throw new InvalidDataException(
                    $"Render settings file '{fullPath}' did not contain a valid settings object.");
            }

            var settings = new RenderSettings();
            file.ApplyTo(settings);
            return settings;
        }

        private static JsonSerializerOptions CreateJsonOptions()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNameCaseInsensitive = true
            };
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }

        private byte[] SerializePersistencePayload()
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
                RenderSettingsFile.FromSettings(this),
                CreateJsonOptions());
            if (payload.Length > MaximumSettingsFileBytes)
            {
                throw new InvalidOperationException(
                    $"Render settings output contains {payload.Length} bytes, exceeding " +
                    $"the {MaximumSettingsFileBytes}-byte limit.");
            }
            return payload;
        }

        private static float ClampScale(float value)
        {
            if (!float.IsFinite(value))
                return 1.0f;
            if (value < 0.5f)
                return 0.5f;
            return value > 1.0f ? 1.0f : value;
        }

        private sealed record RenderSettingsFile
        {
            // A missing version is legacy by definition. Version 4 adds explicit
            // XYZ serialization for authored Simple-DDGI volumes; Vector3 exposes
            // fields rather than properties and therefore cannot safely be left to
            // the default System.Text.Json contract. Version 5 makes the four
            // material-GI V2 rollout switches fail-closed; release qualification
            // remains an external, non-persisted policy. Version 6 persists the
            // independently configurable transparent/decal DDGI receiver policy.
            // Version 7 persists the procedural-atmosphere authoring contract,
            // including explicit XYZ values for its vector fields. Version 8
            // persists independent decal shadow reception; material isolation
            // remains an invocation-scoped benchmark override. Version 9
            // replaces DDGI's overloaded light cap and rough-specular boolean
            // with typed content-dependent modes and independent budgets.
            // Version 10 adds requested advanced-GI modes plus evidence IDs;
            // the previous experiment booleans remain read-compatible aliases.
            // Version 11 replaces the directional ShadowsEnabled scalar with a
            // complete nested directional-shadow production contract. Version
            // 13 adds finite-sun diameter scaling and adaptive per-cascade PCF;
            // older nested shadow objects default to scale 1 and constant radius.
            // Version 14 adds C5 presets and bounded advanced overrides and
            // invalidates every older C5 qualification ID. Version 15 makes
            // explicit adaptive C5 the default and upgrades older
            // AutoQualified requests while preserving explicit opt-outs.
            // Version 16 persists the froxel volumetric-fog contract. Version 17
            // persists the hybrid-reflection mode, quality budgets, and filters.
            // Version 18 persists bounded thick-transmission, nested-media,
            // water-boundary, and optional RGB-dispersion budgets. Version 19
            // persists the bounded transparent scene-reflection ray budget.
            // Version 20 independently bounds transparent SSR Hi-Z samples;
            // missing values inherit the selected preset's safe budget.
            // Version 21 persists the distinct GTAO preset and its safe,
            // default-off bent-normal lighting gate. Version 22 promotes the
            // preset-owned GTAO/bent-normal, receiver-cache, C5 local scheduling,
            // meshlet cone, and transparency partition defaults while retaining
            // explicit current-schema overrides. Version 23 persists automatic,
            // resolution-aware cooked LOD selection and its pixel-error budget;
            // older files retain their distance-based behavior.
            public int? Version { get; init; }
            public RenderQualityPreset QualityPreset { get; init; } = RenderQualityPreset.DdgiHigh;
            public float ResolutionScale { get; init; } = 1.0f;
            public DynamicResolutionFile DynamicResolution { get; init; } = new();
            public ToneMapper ToneMapper { get; init; } = ToneMapper.AcesFitted;
            public float Exposure { get; init; } = 1.0f;
            public bool AutoExposureEnabled { get; init; } = true;
            public AntiAliasingMode AntiAliasingMode { get; init; } = AntiAliasingMode.SmaaMedium;
            public bool BloomEnabled { get; init; } = true;
            public bool AmbientOcclusionEnabled { get; init; } = true;
            public AmbientOcclusionFile? AmbientOcclusion { get; init; }
            public EnvironmentFile? Environment { get; init; }
            public GlobalIlluminationFile? GlobalIllumination { get; init; }
            public bool FogEnabled { get; init; }
            public FogFile? Fog { get; init; }
            public bool ReflectionsEnabled { get; init; } = true;
            public ReflectionSettingsFile? Reflections { get; init; }
            public ShadowSettingsFile? Shadows { get; init; }
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public bool? ShadowsEnabled { get; init; }
            public bool ParticlesEnabled { get; init; } = true;
            public bool? MeshletNormalConeCullingEnabled { get; init; }
            public SpecularAntialiasingMode? SpecularAntialiasingMode { get; init; }
            public VariableRateShadingMode? VariableRateShadingMode { get; init; }
            public TransparencySettingsFile? Transparency { get; init; }
            public bool? TransparentReceiveGlobalIllumination { get; init; }
            public bool? DecalReceiveGlobalIllumination { get; init; }
            public bool? DecalReceiveShadows { get; init; }
            public FoliageFile Foliage { get; init; } = new();
            public SceneSubmissionFile SceneSubmission { get; init; } = new();
            public HiZOcclusionFile HiZOcclusion { get; init; } = new();
            public AsyncComputeFile AsyncCompute { get; init; } = new();
            public bool GpuMeshletCountersEnabled { get; init; }
            public bool DdgiForwardEstimateCountersEnabled { get; init; }

            public static RenderSettingsFile FromSettings(RenderSettings settings)
            {
                return new RenderSettingsFile
                {
                    Version = SerializationVersion,
                    QualityPreset = settings.QualityPreset,
                    ResolutionScale = settings.ResolutionScale,
                    DynamicResolution = DynamicResolutionFile.FromSettings(settings.DynamicResolution),
                    ToneMapper = settings.ToneMapper,
                    Exposure = settings.Exposure,
                    AutoExposureEnabled = settings.AutoExposure.Enabled,
                    AntiAliasingMode = settings.AntiAliasing.Mode,
                    BloomEnabled = settings.Bloom.Enabled,
                    AmbientOcclusionEnabled = settings.AmbientOcclusion.Enabled,
                    AmbientOcclusion = AmbientOcclusionFile.FromSettings(
                        settings.AmbientOcclusion),
                    Environment = EnvironmentFile.FromSettings(settings.Environment),
                    GlobalIllumination = GlobalIlluminationFile.FromSettings(settings.GlobalIllumination),
                    FogEnabled = settings.Fog.Enabled,
                    Fog = FogFile.FromSettings(settings.Fog),
                    ReflectionsEnabled = settings.Reflections.Enabled,
                    Reflections = ReflectionSettingsFile.FromSettings(settings.Reflections),
                    Shadows = ShadowSettingsFile.FromSettings(settings.Shadows),
                    ParticlesEnabled = settings.Particles.Enabled,
                    MeshletNormalConeCullingEnabled =
                        settings.MeshletNormalConeCullingEnabled,
                    SpecularAntialiasingMode =
                        settings.Materials.SpecularAntialiasingMode,
                    VariableRateShadingMode =
                        settings.Raster.VariableRateShadingMode,
                    Transparency = TransparencySettingsFile.FromSettings(
                        settings.Transparency),
                    TransparentReceiveGlobalIllumination =
                        settings.Transparency.ReceiveGlobalIllumination,
                    DecalReceiveGlobalIllumination =
                        settings.Decals.ReceiveGlobalIllumination,
                    DecalReceiveShadows = settings.Decals.ReceiveShadows,
                    Foliage = FoliageFile.FromSettings(settings.Foliage),
                    SceneSubmission = SceneSubmissionFile.FromSettings(settings.SceneSubmission),
                    HiZOcclusion = HiZOcclusionFile.FromSettings(settings.HiZOcclusion),
                    AsyncCompute = AsyncComputeFile.FromSettings(settings.AsyncCompute),
                    GpuMeshletCountersEnabled = settings.Diagnostics.GpuMeshletCountersEnabled,
                    DdgiForwardEstimateCountersEnabled = settings.Diagnostics.DdgiForwardEstimateCountersEnabled
                };
            }

            public void ApplyTo(RenderSettings settings)
            {
                settings.ApplyQualityPreset(QualityPreset);
                settings.ResolutionScale = ResolutionScale;
                DynamicResolution.ApplyTo(settings.DynamicResolution);
                settings.ToneMapper = ToneMapper;
                settings.Exposure = Exposure;
                settings.AutoExposure.Enabled = AutoExposureEnabled;
                settings.AntiAliasing.Mode = AntiAliasingMode;
                settings.Bloom.Enabled = BloomEnabled;
                if (Version.GetValueOrDefault() >= 21 &&
                    AmbientOcclusion != null)
                {
                    AmbientOcclusion.ApplyTo(
                        settings.AmbientOcclusion,
                        Version.GetValueOrDefault());
                }
                else
                {
                    settings.AmbientOcclusion.Enabled =
                        AmbientOcclusionEnabled;
                }
                Environment?.ApplyTo(settings.Environment);
                GlobalIllumination?.ApplyTo(
                    settings.GlobalIllumination,
                    Version.GetValueOrDefault());
                // Rollout authority is never persisted. This also clears V2
                // booleans from current-version files so no raw consumer can
                // observe an unauthenticated feature request during startup.
                settings.GlobalIllumination.UseLegacyMaterialGiRollout();
                settings.GlobalIllumination.UseQualifiedContentDependentBaseline();
                if (Fog != null)
                    Fog.ApplyTo(settings.Fog);
                else
                    settings.Fog.Enabled = FogEnabled;
                if (Version.GetValueOrDefault() >= 17 && Reflections != null)
                {
                    Reflections.ApplyTo(settings.Reflections);
                }
                else
                {
                    // Legacy files persisted only the master switch. Preserve the
                    // selected preset's reflection mode and apply the old opt-in.
                    settings.Reflections.Enabled = ReflectionsEnabled;
                }
                if (Shadows != null)
                {
                    Shadows.ApplyTo(settings.Shadows);
                }
                else if (ShadowsEnabled.HasValue)
                {
                    // Version 10 and older persisted only the enable switch. All
                    // other directional controls retain preset/default values and
                    // legacy rendering behavior.
                    settings.Shadows.DirectionalShadowsEnabled = ShadowsEnabled.Value;
                    settings.Shadows.RequestedDirectionalShadowMode =
                        DirectionalShadowMode.Cascaded;
                    settings.Shadows.DirectionalFilterMode =
                        DirectionalShadowFilterMode.LegacyBoxPcf;
                    settings.Shadows.DirectionalBiasMode =
                        DirectionalShadowBiasMode.Legacy;
                    settings.Shadows.DirectionalPcfRadiusMode =
                        DirectionalPcfRadiusMode.Constant;
                    settings.Shadows.DirectionalSoftAngularDiameterScale = 1f;
                }
                settings.Particles.Enabled = ParticlesEnabled;
                if (Version.GetValueOrDefault() >= 22 &&
                    MeshletNormalConeCullingEnabled.HasValue)
                {
                    settings.MeshletNormalConeCullingEnabled =
                        MeshletNormalConeCullingEnabled.Value;
                }
                settings.Materials.SpecularAntialiasingMode =
                    Version.GetValueOrDefault() >= 23
                        ? SpecularAntialiasingMode ??
                            global::Njulf.Rendering.Data.SpecularAntialiasingMode.GeometricVariance
                        : global::Njulf.Rendering.Data.SpecularAntialiasingMode.Off;
                if (Version.GetValueOrDefault() >= 23 &&
                    VariableRateShadingMode.HasValue)
                {
                    settings.Raster.VariableRateShadingMode =
                        VariableRateShadingMode.Value;
                }
                if (Version.GetValueOrDefault() >= 18 && Transparency != null)
                {
                    Transparency.ApplyTo(
                        settings.Transparency,
                        Version.GetValueOrDefault());
                }
                else if (TransparentReceiveGlobalIllumination.HasValue)
                {
                    settings.Transparency.ReceiveGlobalIllumination =
                        TransparentReceiveGlobalIllumination.Value;
                }
                if (DecalReceiveGlobalIllumination.HasValue)
                {
                    settings.Decals.ReceiveGlobalIllumination =
                        DecalReceiveGlobalIllumination.Value;
                }
                if (DecalReceiveShadows.HasValue)
                    settings.Decals.ReceiveShadows = DecalReceiveShadows.Value;
                Foliage.ApplyTo(settings.Foliage);
                SceneSubmission.ApplyTo(
                    settings.SceneSubmission,
                    Version.GetValueOrDefault());
                HiZOcclusion.ApplyTo(settings.HiZOcclusion);
                // Version 3 introduced Auto as the fresh-install default. Older files did not
                // have a trustworthy policy field, so retain their graphics-only behavior when
                // Mode is absent instead of silently changing an existing installation.
                AsyncCompute.ApplyTo(settings.AsyncCompute, missingModeMeansDisabled: !Version.HasValue || Version.Value < 3);
                settings.Diagnostics.GpuMeshletCountersEnabled = GpuMeshletCountersEnabled;
                settings.Diagnostics.DdgiForwardEstimateCountersEnabled = DdgiForwardEstimateCountersEnabled;
            }
        }

        private sealed record AmbientOcclusionFile
        {
            public bool Enabled { get; init; } = true;
            public AmbientOcclusionMode Mode { get; init; } =
                AmbientOcclusionMode.Ssao;
            public float ResolutionScale { get; init; } = 0.5f;
            public float Radius { get; init; } = 0.75f;
            public float Intensity { get; init; } = 1.0f;
            public float Bias { get; init; } = 0.03f;
            public float Power { get; init; } = 1.2f;
            public int SampleCount { get; init; } = 16;
            public int BlurRadius { get; init; } = 2;
            public float DepthSigma { get; init; } = 2.0f;
            public float NormalSigma { get; init; } = 32.0f;
            public bool UseSceneNormals { get; init; }
            public AmbientOcclusionDebugView DebugView { get; init; }
            public GtaoQualityPreset GtaoQualityPreset { get; init; } =
                GtaoQualityPreset.Balanced;
            public float GtaoThickness { get; init; } = 0.15f;
            public float GtaoFalloff { get; init; } = 1.0f;
            public AmbientOcclusionBentNormalMode BentNormalMode { get; init; }

            public static AmbientOcclusionFile FromSettings(
                AmbientOcclusionSettings settings) => new()
            {
                Enabled = settings.Enabled,
                Mode = settings.Mode,
                ResolutionScale = settings.ResolutionScale,
                Radius = settings.Radius,
                Intensity = settings.Intensity,
                Bias = settings.Bias,
                Power = settings.Power,
                SampleCount = settings.SampleCount,
                BlurRadius = settings.BlurRadius,
                DepthSigma = settings.DepthSigma,
                NormalSigma = settings.NormalSigma,
                UseSceneNormals = settings.UseSceneNormals,
                DebugView = settings.DebugView,
                GtaoQualityPreset = settings.GtaoQualityPreset,
                GtaoThickness = settings.GtaoThickness,
                GtaoFalloff = settings.GtaoFalloff,
                BentNormalMode = settings.BentNormalMode
            };

            public void ApplyTo(
                AmbientOcclusionSettings settings,
                int sourceVersion)
            {
                settings.Enabled = Enabled;
                settings.ResolutionScale = ResolutionScale;
                settings.Radius = Radius;
                settings.Intensity = Intensity;
                settings.Bias = Bias;
                settings.Power = Power;
                settings.SampleCount = SampleCount;
                settings.BlurRadius = BlurRadius;
                settings.DepthSigma = DepthSigma;
                settings.NormalSigma = NormalSigma;
                settings.UseSceneNormals = UseSceneNormals;
                settings.DebugView = Enum.IsDefined(DebugView)
                    ? DebugView
                    : AmbientOcclusionDebugView.None;
                settings.GtaoThickness = GtaoThickness;
                settings.GtaoFalloff = GtaoFalloff;
                // Schema 21 wrote SSAO/Balanced/Off even when those values were
                // only the former conservative defaults. Let the selected preset
                // own those fields during promotion. A non-SSAO request remains
                // an intentional legacy override.
                if (sourceVersion >= 22 || Mode != AmbientOcclusionMode.Ssao)
                {
                    settings.Mode = Enum.IsDefined(Mode)
                        ? Mode
                        : AmbientOcclusionMode.Ssao;
                    settings.GtaoQualityPreset = Enum.IsDefined(GtaoQualityPreset)
                        ? GtaoQualityPreset
                        : GtaoQualityPreset.Balanced;
                    settings.BentNormalMode = Enum.IsDefined(BentNormalMode)
                        ? BentNormalMode
                        : AmbientOcclusionBentNormalMode.Off;
                }
            }
        }

        private sealed record FogFile
        {
            public bool Enabled { get; init; }
            public FogTechnique Technique { get; init; } = FogTechnique.Auto;
            public FogMode Mode { get; init; } = FogMode.DistanceAndHeight;
            public FogColorMode ColorMode { get; init; } = FogColorMode.SkyAndConstantBlend;
            public Vector3File Color { get; init; } = new() { X = 0.62f, Y = 0.72f, Z = 0.82f };
            public float ColorBlend { get; init; } = 0.5f;
            public float Density { get; init; } = 0.015f;
            public float StartDistance { get; init; } = 5f;
            public float EndDistance { get; init; } = 250f;
            public float Height { get; init; }
            public float HeightFalloff { get; init; } = 0.12f;
            public float HeightDensity { get; init; } = 0.04f;
            public float MaxOpacity { get; init; } = 0.85f;
            public bool DirectionalInscatteringEnabled { get; init; } = true;
            public Vector3File DirectionalInscatteringColor { get; init; } =
                new() { X = 1f, Y = 0.88f, Z = 0.68f };
            public Vector3File DirectionalInscatteringDirection { get; init; } =
                new();
            public float DirectionalInscatteringIntensity { get; init; } = 0.35f;
            public float DirectionalInscatteringExponent { get; init; } = 8f;
            public FogDebugView DebugView { get; init; } = FogDebugView.None;
            public VolumetricFogFile Volumetric { get; init; } = new();

            public static FogFile FromSettings(FogSettings settings) => new()
            {
                Enabled = settings.Enabled,
                Technique = settings.Technique,
                Mode = settings.Mode,
                ColorMode = settings.ColorMode,
                Color = Vector3File.FromVector3(settings.Color),
                ColorBlend = settings.ColorBlend,
                Density = settings.Density,
                StartDistance = settings.StartDistance,
                EndDistance = settings.EndDistance,
                Height = settings.Height,
                HeightFalloff = settings.HeightFalloff,
                HeightDensity = settings.HeightDensity,
                MaxOpacity = settings.MaxOpacity,
                DirectionalInscatteringEnabled =
                    settings.DirectionalInscatteringEnabled,
                DirectionalInscatteringColor = Vector3File.FromVector3(
                    settings.DirectionalInscatteringColor),
                DirectionalInscatteringDirection = Vector3File.FromVector3(
                    settings.DirectionalInscatteringDirection),
                DirectionalInscatteringIntensity =
                    settings.DirectionalInscatteringIntensity,
                DirectionalInscatteringExponent =
                    settings.DirectionalInscatteringExponent,
                DebugView = settings.DebugView,
                Volumetric = VolumetricFogFile.FromSettings(settings.Volumetric)
            };

            public void ApplyTo(FogSettings settings)
            {
                settings.Enabled = Enabled;
                settings.Technique = Enum.IsDefined(Technique) ? Technique : FogTechnique.Auto;
                settings.Mode = Enum.IsDefined(Mode) ? Mode : FogMode.DistanceAndHeight;
                settings.ColorMode = Enum.IsDefined(ColorMode) ? ColorMode : FogColorMode.SkyAndConstantBlend;
                settings.Color = Color.ToVector3();
                settings.ColorBlend = ColorBlend;
                settings.Density = Density;
                settings.StartDistance = StartDistance;
                settings.EndDistance = EndDistance;
                settings.Height = Height;
                settings.HeightFalloff = HeightFalloff;
                settings.HeightDensity = HeightDensity;
                settings.MaxOpacity = MaxOpacity;
                settings.DirectionalInscatteringEnabled =
                    DirectionalInscatteringEnabled;
                settings.DirectionalInscatteringColor =
                    DirectionalInscatteringColor.ToVector3();
                settings.DirectionalInscatteringDirection =
                    DirectionalInscatteringDirection.ToVector3();
                settings.DirectionalInscatteringIntensity =
                    DirectionalInscatteringIntensity;
                settings.DirectionalInscatteringExponent =
                    DirectionalInscatteringExponent;
                settings.DebugView = Enum.IsDefined(DebugView)
                    ? DebugView
                    : FogDebugView.None;
                Volumetric.ApplyTo(settings.Volumetric);
            }
        }

        private sealed record VolumetricFogFile
        {
            // Kept only so version-16 files deserialize without an unknown
            // member. Qualification is deliberately not restored from disk.
            public bool? ProfileQualified { get; init; }
            public float MaxDistance { get; init; } = 250f;
            public float BaseExtinctionPerMeter { get; init; } = 0.015f;
            public float HeightExtinctionPerMeter { get; init; } = 0.04f;
            public float Height { get; init; }
            public float HeightFalloff { get; init; } = 0.12f;
            public Vector3File ScatteringAlbedo { get; init; } = new() { X = 0.9f, Y = 0.92f, Z = 0.95f };
            public float Anisotropy { get; init; } = 0.2f;
            public Vector3File GlobalWind { get; init; } = new();
            public float NoiseScale { get; init; } = 0.035f;
            public float NoiseStrength { get; init; } = 0.15f;
            public float NoiseContrast { get; init; } = 1f;
            public float SelfShadowDistance { get; init; } = 64f;
            public float TemporalHistoryWeight { get; init; } = 0.9f;
            public int MultipleScatteringIterations { get; init; }
            public float MultipleScatteringEnergyLimit { get; init; } = 0.5f;
            public int DebugSlice { get; init; } = -1;
            public FogDebugProjection DebugProjection { get; init; } =
                FogDebugProjection.MaxAlongRay;

            public static VolumetricFogFile FromSettings(VolumetricFogSettings settings) => new()
            {
                MaxDistance = settings.MaxDistance,
                BaseExtinctionPerMeter = settings.BaseExtinctionPerMeter,
                HeightExtinctionPerMeter = settings.HeightExtinctionPerMeter,
                Height = settings.Height,
                HeightFalloff = settings.HeightFalloff,
                ScatteringAlbedo = Vector3File.FromVector3(settings.ScatteringAlbedo),
                Anisotropy = settings.Anisotropy,
                GlobalWind = Vector3File.FromVector3(settings.GlobalWind),
                NoiseScale = settings.NoiseScale,
                NoiseStrength = settings.NoiseStrength,
                NoiseContrast = settings.NoiseContrast,
                SelfShadowDistance = settings.SelfShadowDistance,
                TemporalHistoryWeight = settings.TemporalHistoryWeight,
                MultipleScatteringIterations = settings.MultipleScatteringIterations,
                MultipleScatteringEnergyLimit = settings.MultipleScatteringEnergyLimit,
                DebugSlice = settings.DebugSlice,
                DebugProjection = settings.DebugProjection
            };

            public void ApplyTo(VolumetricFogSettings settings)
            {
                settings.MaxDistance = MaxDistance;
                settings.BaseExtinctionPerMeter = BaseExtinctionPerMeter;
                settings.HeightExtinctionPerMeter = HeightExtinctionPerMeter;
                settings.Height = Height;
                settings.HeightFalloff = HeightFalloff;
                settings.ScatteringAlbedo = ScatteringAlbedo.ToVector3();
                settings.Anisotropy = Anisotropy;
                settings.GlobalWind = GlobalWind.ToVector3();
                settings.NoiseScale = NoiseScale;
                settings.NoiseStrength = NoiseStrength;
                settings.NoiseContrast = NoiseContrast;
                settings.SelfShadowDistance = SelfShadowDistance;
                settings.TemporalHistoryWeight = TemporalHistoryWeight;
                settings.MultipleScatteringIterations = MultipleScatteringIterations;
                settings.MultipleScatteringEnergyLimit = MultipleScatteringEnergyLimit;
                settings.DebugSlice = DebugSlice;
                settings.DebugProjection = Enum.IsDefined(DebugProjection)
                    ? DebugProjection
                    : FogDebugProjection.MaxAlongRay;
            }
        }

        private sealed record ReflectionSettingsFile
        {
            public bool Enabled { get; init; } = true;
            public ReflectionMode Mode { get; init; } = ReflectionMode.StaticProbes;
            public int MaxProbes { get; init; } = 8;
            public int MaxProbesPerPixel { get; init; } = 2;
            public uint ProbeResolution { get; init; } = 128;
            public float Intensity { get; init; } = 1.0f;
            public float GlobalFallbackIntensity { get; init; } = 1.0f;
            public bool BoxProjectionEnabled { get; init; } = true;
            public bool ProbeBlendingEnabled { get; init; } = true;
            public float SsrFullResolutionRoughness { get; init; } = 0.2f;
            public float SsrHalfResolutionRoughness { get; init; } = 0.5f;
            public float SsrQuarterResolutionRoughness { get; init; } = 0.8f;
            public int SsrMaxSteps { get; init; } = 64;
            public float SsrMaxDistance { get; init; } = 75.0f;
            public float SsrConfidenceThreshold { get; init; } = 0.75f;
            public float RayQueryPixelBudgetFraction { get; init; } = 0.0078125f;
            public int RayQueryHitLightLimit { get; init; } = 2;
            public int TemporalHistoryLength { get; init; } = 16;
            public int SpatialFilterPassCount { get; init; } = 2;

            public static ReflectionSettingsFile FromSettings(ReflectionSettings settings) => new()
            {
                Enabled = settings.Enabled,
                Mode = settings.Mode,
                MaxProbes = settings.MaxProbes,
                MaxProbesPerPixel = settings.MaxProbesPerPixel,
                ProbeResolution = settings.ProbeResolution,
                Intensity = settings.Intensity,
                GlobalFallbackIntensity = settings.GlobalFallbackIntensity,
                BoxProjectionEnabled = settings.BoxProjectionEnabled,
                ProbeBlendingEnabled = settings.ProbeBlendingEnabled,
                SsrFullResolutionRoughness = settings.SsrFullResolutionRoughness,
                SsrHalfResolutionRoughness = settings.SsrHalfResolutionRoughness,
                SsrQuarterResolutionRoughness = settings.SsrQuarterResolutionRoughness,
                SsrMaxSteps = settings.SsrMaxSteps,
                SsrMaxDistance = settings.SsrMaxDistance,
                SsrConfidenceThreshold = settings.SsrConfidenceThreshold,
                RayQueryPixelBudgetFraction = settings.RayQueryPixelBudgetFraction,
                RayQueryHitLightLimit = settings.RayQueryHitLightLimit,
                TemporalHistoryLength = settings.TemporalHistoryLength,
                SpatialFilterPassCount = settings.SpatialFilterPassCount
            };

            public void ApplyTo(ReflectionSettings settings)
            {
                settings.Enabled = Enabled;
                settings.Mode = Enum.IsDefined(Mode) ? Mode : ReflectionMode.StaticProbes;
                settings.MaxProbes = MaxProbes;
                settings.MaxProbesPerPixel = MaxProbesPerPixel;
                settings.ProbeResolution = ProbeResolution;
                settings.Intensity = Intensity;
                settings.GlobalFallbackIntensity = GlobalFallbackIntensity;
                settings.BoxProjectionEnabled = BoxProjectionEnabled;
                settings.ProbeBlendingEnabled = ProbeBlendingEnabled;
                settings.SsrFullResolutionRoughness = SsrFullResolutionRoughness;
                settings.SsrHalfResolutionRoughness = SsrHalfResolutionRoughness;
                settings.SsrQuarterResolutionRoughness = SsrQuarterResolutionRoughness;
                settings.SsrMaxSteps = SsrMaxSteps;
                settings.SsrMaxDistance = SsrMaxDistance;
                settings.SsrConfidenceThreshold = SsrConfidenceThreshold;
                settings.RayQueryPixelBudgetFraction = RayQueryPixelBudgetFraction;
                settings.RayQueryHitLightLimit = RayQueryHitLightLimit;
                settings.TemporalHistoryLength = TemporalHistoryLength;
                settings.SpatialFilterPassCount = SpatialFilterPassCount;
            }
        }

        private sealed record TransparencySettingsFile
        {
            public bool Enabled { get; init; } = true;
            public TransparencyMode Mode { get; init; } =
                TransparencyMode.SortedAlphaBlend;
            public TransparencyDebugView DebugView { get; init; }
            public bool ReceiveShadows { get; init; } = true;
            public bool ReceiveGlobalIllumination { get; init; } = true;
            public bool SampleReflections { get; init; } = true;
            public int? SceneReflectionRayTaskBudget { get; init; }
            public int? SceneReflectionSsrSampleBudget { get; init; }
            public bool SortPerMeshlet { get; init; } = true;
            public bool? PipelinePartitioningEnabled { get; init; }
            public int MaxTransparentMeshlets { get; init; } = 262_144;
            public float AlphaDiscardThreshold { get; init; } = 0.001f;
            public ThickTransmissionMode ThickTransmissionMode { get; init; } =
                ThickTransmissionMode.RayQuery;
            public DispersionMode DispersionMode { get; init; } =
                DispersionMode.Off;
            public int ThickTransmissionRayTaskBudget { get; init; } = 262_144;
            public int ThickTransmissionMaximumInterfaces { get; init; } =
                BoundedDielectricMediaStack.MaximumInterfaces;
            public int ThickTransmissionMaximumMediaDepth { get; init; } =
                BoundedDielectricMediaStack.MaximumDepth;
            public int ThickTransmissionMaximumCandidatesPerInterface
            {
                get;
                init;
            } = BoundedDielectricMediaStack.MaximumCandidatesPerInterface;
            public float ThickTransmissionMaximumDistance { get; init; } = 100f;
            public ulong ThickTransmissionMemoryBudgetBytes { get; init; } =
                128UL * 1024UL * 1024UL;

            public static TransparencySettingsFile FromSettings(
                TransparencySettings settings) => new()
            {
                Enabled = settings.Enabled,
                Mode = settings.Mode,
                DebugView = settings.DebugView,
                ReceiveShadows = settings.ReceiveShadows,
                ReceiveGlobalIllumination = settings.ReceiveGlobalIllumination,
                SampleReflections = settings.SampleReflections,
                SceneReflectionRayTaskBudget =
                    settings.SceneReflectionRayTaskBudget,
                SceneReflectionSsrSampleBudget =
                    settings.SceneReflectionSsrSampleBudget,
                SortPerMeshlet = settings.SortPerMeshlet,
                PipelinePartitioningEnabled =
                    settings.PipelinePartitioningEnabled,
                MaxTransparentMeshlets = settings.MaxTransparentMeshlets,
                AlphaDiscardThreshold = settings.AlphaDiscardThreshold,
                ThickTransmissionMode = settings.ThickTransmissionMode,
                DispersionMode = settings.DispersionMode,
                ThickTransmissionRayTaskBudget =
                    settings.ThickTransmissionRayTaskBudget,
                ThickTransmissionMaximumInterfaces =
                    settings.ThickTransmissionMaximumInterfaces,
                ThickTransmissionMaximumMediaDepth =
                    settings.ThickTransmissionMaximumMediaDepth,
                ThickTransmissionMaximumCandidatesPerInterface =
                    settings.ThickTransmissionMaximumCandidatesPerInterface,
                ThickTransmissionMaximumDistance =
                    settings.ThickTransmissionMaximumDistance,
                ThickTransmissionMemoryBudgetBytes =
                    settings.ThickTransmissionMemoryBudgetBytes
            };

            public void ApplyTo(
                TransparencySettings settings,
                int sourceVersion)
            {
                settings.Enabled = Enabled;
                settings.Mode = Enum.IsDefined(Mode)
                    ? Mode : TransparencyMode.SortedAlphaBlend;
                settings.DebugView = Enum.IsDefined(DebugView)
                    ? DebugView : TransparencyDebugView.None;
                settings.ReceiveShadows = ReceiveShadows;
                settings.ReceiveGlobalIllumination = ReceiveGlobalIllumination;
                settings.SampleReflections = SampleReflections;
                if (SceneReflectionRayTaskBudget.HasValue)
                {
                    settings.SceneReflectionRayTaskBudget =
                        SceneReflectionRayTaskBudget.Value;
                }
                if (SceneReflectionSsrSampleBudget.HasValue)
                {
                    settings.SceneReflectionSsrSampleBudget =
                        SceneReflectionSsrSampleBudget.Value;
                }
                settings.SortPerMeshlet = SortPerMeshlet;
                if (PipelinePartitioningEnabled == true ||
                    sourceVersion >= 22 && PipelinePartitioningEnabled.HasValue)
                {
                    settings.PipelinePartitioningEnabled =
                        PipelinePartitioningEnabled.Value;
                }
                settings.MaxTransparentMeshlets = MaxTransparentMeshlets;
                settings.AlphaDiscardThreshold = AlphaDiscardThreshold;
                settings.ThickTransmissionMode =
                    Enum.IsDefined(ThickTransmissionMode)
                        ? ThickTransmissionMode
                        : ThickTransmissionMode.Approximation;
                settings.DispersionMode = Enum.IsDefined(DispersionMode)
                    ? DispersionMode : DispersionMode.Off;
                settings.ThickTransmissionRayTaskBudget =
                    ThickTransmissionRayTaskBudget;
                settings.ThickTransmissionMaximumInterfaces =
                    ThickTransmissionMaximumInterfaces;
                settings.ThickTransmissionMaximumMediaDepth =
                    ThickTransmissionMaximumMediaDepth;
                settings.ThickTransmissionMaximumCandidatesPerInterface =
                    ThickTransmissionMaximumCandidatesPerInterface;
                settings.ThickTransmissionMaximumDistance =
                    ThickTransmissionMaximumDistance;
                settings.ThickTransmissionMemoryBudgetBytes =
                    ThickTransmissionMemoryBudgetBytes;
            }
        }

        private sealed record ShadowSettingsFile
        {
            public bool DirectionalShadowsEnabled { get; init; } = true;
            public DirectionalShadowMode RequestedDirectionalShadowMode { get; init; } =
                DirectionalShadowMode.Cascaded;
            public DirectionalCsmTemporalMode DirectionalCsmTemporalMode { get; init; } =
                DirectionalCsmTemporalMode.Disabled;
            public DirectionalShadowFilterMode DirectionalFilterMode { get; init; } =
                DirectionalShadowFilterMode.LegacyBoxPcf;
            public DirectionalShadowBiasMode DirectionalBiasMode { get; init; } =
                DirectionalShadowBiasMode.Legacy;
            public DirectionalPcfRadiusMode DirectionalPcfRadiusMode { get; init; } =
                DirectionalPcfRadiusMode.Constant;
            public uint DirectionalShadowMapSize { get; init; } = 2048;
            public int DirectionalCascadeCount { get; init; } = 2;
            public float MaxShadowDistance { get; init; } = 80f;
            public float DirectionalCascadeBlendFraction { get; init; } = 0.12f;
            public float DirectionalCascadeSplitLambda { get; init; } = 0.5f;
            public float DirectionalCasterExtrusionDistance { get; init; } = 80f;
            public float DirectionalContactShadowDistance { get; init; } = 3f;
            public float DirectionalSoftAngularDiameterScale { get; init; } = 1f;
            public int DirectionalSoftRecoveryRayCount { get; init; } = 2;
            public int DirectionalSoftHistoryLength { get; init; } = 16;
            public int DirectionalSoftSpatialPassCount { get; init; } = 3;
            public int DirectionalTransparentSoftRayCount { get; init; } = 4;
            public float NormalBias { get; init; } = 0.03f;
            public float SlopeScaledDepthBias { get; init; } = 1.5f;
            public float ConstantDepthBias { get; init; } = 0.0005f;
            public int PcfRadius { get; init; } = 1;
            public bool AreaShadowsEnabled { get; init; } = true;
            public int MaxShadowedAreaLights { get; init; } = 2;
            public int AreaShadowSampleCount { get; init; } = 1;

            public static ShadowSettingsFile FromSettings(ShadowSettings settings) => new()
            {
                DirectionalShadowsEnabled = settings.DirectionalShadowsEnabled,
                RequestedDirectionalShadowMode = settings.RequestedDirectionalShadowMode,
                DirectionalCsmTemporalMode = settings.DirectionalCsmTemporalMode,
                DirectionalFilterMode = settings.DirectionalFilterMode,
                DirectionalBiasMode = settings.DirectionalBiasMode,
                DirectionalPcfRadiusMode = settings.DirectionalPcfRadiusMode,
                DirectionalShadowMapSize = settings.DirectionalShadowMapSize,
                DirectionalCascadeCount = settings.DirectionalCascadeCount,
                MaxShadowDistance = settings.MaxShadowDistance,
                DirectionalCascadeBlendFraction = settings.DirectionalCascadeBlendFraction,
                DirectionalCascadeSplitLambda = settings.DirectionalCascadeSplitLambda,
                DirectionalCasterExtrusionDistance = settings.DirectionalCasterExtrusionDistance,
                DirectionalContactShadowDistance = settings.DirectionalContactShadowDistance,
                DirectionalSoftAngularDiameterScale = settings.DirectionalSoftAngularDiameterScale,
                DirectionalSoftRecoveryRayCount = settings.DirectionalSoftRecoveryRayCount,
                DirectionalSoftHistoryLength = settings.DirectionalSoftHistoryLength,
                DirectionalSoftSpatialPassCount = settings.DirectionalSoftSpatialPassCount,
                DirectionalTransparentSoftRayCount = settings.DirectionalTransparentSoftRayCount,
                NormalBias = settings.NormalBias,
                SlopeScaledDepthBias = settings.SlopeScaledDepthBias,
                ConstantDepthBias = settings.ConstantDepthBias,
                PcfRadius = settings.PcfRadius,
                AreaShadowsEnabled = settings.AreaShadowsEnabled,
                MaxShadowedAreaLights = settings.MaxShadowedAreaLights,
                AreaShadowSampleCount = settings.AreaShadowSampleCount
            };

            public void ApplyTo(ShadowSettings settings)
            {
                settings.DirectionalShadowsEnabled = DirectionalShadowsEnabled;
                settings.RequestedDirectionalShadowMode = RequestedDirectionalShadowMode;
                settings.DirectionalCsmTemporalMode = DirectionalCsmTemporalMode;
                settings.DirectionalFilterMode = DirectionalFilterMode;
                settings.DirectionalBiasMode = DirectionalBiasMode;
                settings.DirectionalPcfRadiusMode = DirectionalPcfRadiusMode;
                settings.DirectionalShadowMapSize = DirectionalShadowMapSize;
                settings.DirectionalCascadeCount = DirectionalCascadeCount;
                settings.MaxShadowDistance = MaxShadowDistance;
                settings.DirectionalCascadeBlendFraction = DirectionalCascadeBlendFraction;
                settings.DirectionalCascadeSplitLambda = DirectionalCascadeSplitLambda;
                settings.DirectionalCasterExtrusionDistance = DirectionalCasterExtrusionDistance;
                settings.DirectionalContactShadowDistance = DirectionalContactShadowDistance;
                settings.DirectionalSoftAngularDiameterScale = DirectionalSoftAngularDiameterScale;
                settings.DirectionalSoftRecoveryRayCount = DirectionalSoftRecoveryRayCount;
                settings.DirectionalSoftHistoryLength = DirectionalSoftHistoryLength;
                settings.DirectionalSoftSpatialPassCount = DirectionalSoftSpatialPassCount;
                settings.DirectionalTransparentSoftRayCount = DirectionalTransparentSoftRayCount;
                settings.NormalBias = NormalBias;
                settings.SlopeScaledDepthBias = SlopeScaledDepthBias;
                settings.ConstantDepthBias = ConstantDepthBias;
                settings.PcfRadius = PcfRadius;
                settings.AreaShadowsEnabled = AreaShadowsEnabled;
                settings.MaxShadowedAreaLights = MaxShadowedAreaLights;
                settings.AreaShadowSampleCount = AreaShadowSampleCount;
            }
        }

        private sealed record EnvironmentFile
        {
            public bool Enabled { get; init; } = true;
            public EnvironmentSourceKind SourceKind { get; init; } =
                EnvironmentSourceKind.ProceduralSky;
            public string? SourcePath { get; init; }
            public EnvironmentTexturePrecision TexturePrecision { get; init; } =
                EnvironmentTexturePrecision.Float16;
            public ProceduralSkySunDriver SunDriver { get; init; } =
                ProceduralSkySunDriver.SceneDirectionalLight;
            public bool AnimateTimeOfDay { get; init; }
            public float Turbidity { get; init; } = 3.0f;
            public float GroundAlbedoX { get; init; } = 0.2f;
            public float GroundAlbedoY { get; init; } = 0.2f;
            public float GroundAlbedoZ { get; init; } = 0.2f;
            public float SunAngularDiameterDegrees { get; init; } = 0.53f;
            public float MoonAngularDiameterDegrees { get; init; } = 0.52f;
            public float TimeOfDayHours { get; init; } = 14.0f;
            public float LatitudeDegrees { get; init; } = 59.9139f;
            public int DayOfYear { get; init; } = 172;
            public float NorthOffsetDegrees { get; init; }
            public float TimeScale { get; init; } = 60.0f;
            public float DirectSunDirectionX { get; init; } = -0.3010859f;
            public float DirectSunDirectionY { get; init; } = 0.7226061f;
            public float DirectSunDirectionZ { get; init; } = 0.6222441f;
            public float AtmosphereIntensity { get; init; } = 1.0f;
            public float SolarIrradianceScale { get; init; } = 14.0f;
            public float MoonIrradianceScale { get; init; } = 0.12f;
            public float StarIntensity { get; init; } = 0.025f;
            public float AirglowIntensity { get; init; } = 0.025f;
            public float GiSunStepDegrees { get; init; } = 0.25f;
            public float GiTargetSourceSweepSeconds { get; init; } = 8.0f;
            public int SpecularPrefilterMipsPerFrame { get; init; } = 1;
            public int SpecularPrefilterTransitionFrames { get; init; } = 8;
            public float SkyIntensity { get; init; } = 1.0f;
            public float DiffuseIntensity { get; init; } = 1.0f;
            public float SpecularIntensity { get; init; } = 1.0f;
            public float RotationRadians { get; init; }
            public uint EnvironmentSize { get; init; } = 1024;
            public uint IrradianceSize { get; init; } = 64;
            public uint PrefilteredSize { get; init; } = 128;
            public uint BrdfLutSize { get; init; } = 256;
            public EnvironmentDebugView DebugView { get; init; } =
                EnvironmentDebugView.None;
            public int DebugMipLevel { get; init; }

            public static EnvironmentFile FromSettings(EnvironmentSettings settings)
            {
                Vector3 ground = settings.GroundAlbedo;
                Vector3 sun = settings.DirectSunDirection;
                return new EnvironmentFile
                {
                    Enabled = settings.Enabled,
                    SourceKind = settings.SourceKind,
                    SourcePath = settings.SourcePath,
                    TexturePrecision = settings.TexturePrecision,
                    SunDriver = settings.SunDriver,
                    AnimateTimeOfDay = settings.AnimateTimeOfDay,
                    Turbidity = settings.Turbidity,
                    GroundAlbedoX = ground.X,
                    GroundAlbedoY = ground.Y,
                    GroundAlbedoZ = ground.Z,
                    SunAngularDiameterDegrees = settings.SunAngularDiameterDegrees,
                    MoonAngularDiameterDegrees = settings.MoonAngularDiameterDegrees,
                    TimeOfDayHours = settings.TimeOfDayHours,
                    LatitudeDegrees = settings.LatitudeDegrees,
                    DayOfYear = settings.DayOfYear,
                    NorthOffsetDegrees = settings.NorthOffsetDegrees,
                    TimeScale = settings.TimeScale,
                    DirectSunDirectionX = sun.X,
                    DirectSunDirectionY = sun.Y,
                    DirectSunDirectionZ = sun.Z,
                    AtmosphereIntensity = settings.AtmosphereIntensity,
                    SolarIrradianceScale = settings.SolarIrradianceScale,
                    MoonIrradianceScale = settings.MoonIrradianceScale,
                    StarIntensity = settings.StarIntensity,
                    AirglowIntensity = settings.AirglowIntensity,
                    GiSunStepDegrees = settings.GiSunStepDegrees,
                    GiTargetSourceSweepSeconds = settings.GiTargetSourceSweepSeconds,
                    SpecularPrefilterMipsPerFrame = settings.SpecularPrefilterMipsPerFrame,
                    SpecularPrefilterTransitionFrames = settings.SpecularPrefilterTransitionFrames,
                    SkyIntensity = settings.SkyIntensity,
                    DiffuseIntensity = settings.DiffuseIntensity,
                    SpecularIntensity = settings.SpecularIntensity,
                    RotationRadians = settings.RotationRadians,
                    EnvironmentSize = settings.EnvironmentSize,
                    IrradianceSize = settings.IrradianceSize,
                    PrefilteredSize = settings.PrefilteredSize,
                    BrdfLutSize = settings.BrdfLutSize,
                    DebugView = settings.DebugView,
                    DebugMipLevel = settings.DebugMipLevel
                };
            }

            public void ApplyTo(EnvironmentSettings settings)
            {
                settings.Enabled = Enabled;
                settings.SourceKind = SourceKind;
                settings.SourcePath = SourcePath;
                settings.TexturePrecision = TexturePrecision;
                settings.SunDriver = SunDriver;
                settings.AnimateTimeOfDay = AnimateTimeOfDay;
                settings.Turbidity = Turbidity;
                settings.GroundAlbedo = new Vector3(
                    GroundAlbedoX,
                    GroundAlbedoY,
                    GroundAlbedoZ);
                settings.SunAngularDiameterDegrees = SunAngularDiameterDegrees;
                settings.MoonAngularDiameterDegrees = MoonAngularDiameterDegrees;
                settings.TimeOfDayHours = TimeOfDayHours;
                settings.LatitudeDegrees = LatitudeDegrees;
                settings.DayOfYear = DayOfYear;
                settings.NorthOffsetDegrees = NorthOffsetDegrees;
                settings.TimeScale = TimeScale;
                settings.DirectSunDirection = new Vector3(
                    DirectSunDirectionX,
                    DirectSunDirectionY,
                    DirectSunDirectionZ);
                settings.AtmosphereIntensity = AtmosphereIntensity;
                settings.SolarIrradianceScale = SolarIrradianceScale;
                settings.MoonIrradianceScale = MoonIrradianceScale;
                settings.StarIntensity = StarIntensity;
                settings.AirglowIntensity = AirglowIntensity;
                settings.GiSunStepDegrees = GiSunStepDegrees;
                settings.GiTargetSourceSweepSeconds = GiTargetSourceSweepSeconds;
                settings.SpecularPrefilterMipsPerFrame = SpecularPrefilterMipsPerFrame;
                settings.SpecularPrefilterTransitionFrames =
                    SpecularPrefilterTransitionFrames;
                settings.SkyIntensity = SkyIntensity;
                settings.DiffuseIntensity = DiffuseIntensity;
                settings.SpecularIntensity = SpecularIntensity;
                settings.RotationRadians = RotationRadians;
                settings.EnvironmentSize = EnvironmentSize;
                settings.IrradianceSize = IrradianceSize;
                settings.PrefilteredSize = PrefilteredSize;
                settings.BrdfLutSize = BrdfLutSize;
                settings.DebugView = DebugView;
                settings.DebugMipLevel = DebugMipLevel;
            }
        }

        private sealed record GlobalIlluminationFile
        {
            public bool Enabled { get; init; } = true;
            public GlobalIlluminationMode Mode { get; init; } = GlobalIlluminationMode.Ddgi;
            public GlobalIlluminationDebugView DebugView { get; init; } = GlobalIlluminationDebugView.None;
            public bool EmergencyGiFallbackEnabled { get; init; }
            public float IndirectIntensity { get; init; } = 1.0f;
            public float EnvironmentFallbackIntensity { get; init; } = 1.0f;
            // Keep partially specified settings files on the DDGI-focused default.
            public bool UseDdgi { get; init; } = true;
            // Nullable preserves quality-preset defaults for legacy files that
            // predate the explicit backend selector. A serialized false remains
            // an explicit opt-out.
            public bool? UseRayQueryBackend { get; init; }
            public DdgiQualityTier DdgiQualityTier { get; init; } = DdgiQualityTier.DdgiHigh;
            public bool GiMaterialTransportV2 { get; init; }
            public bool GiEmissiveMeshSampling { get; init; }
            public bool DdgiProbeClassificationEnabled { get; init; } = true;
            public bool DdgiProbeRelocationEnabled { get; init; }
            public bool DdgiProbeL1MetadataEnabled { get; init; } = true;
            public bool DdgiCameraRelativeEnabled { get; init; } = true;
            public bool DdgiAdaptiveBudgetingEnabled { get; init; } = true;
            public bool DdgiThinWallPolicyEnabled { get; init; } = true;
            public bool DdgiAsyncComputeEnabled { get; init; } = true;
            public bool DdgiAlphaMaskedTransportEnabled { get; init; } = true;
            // Nullable preserves the authored default for older settings files
            // while allowing an explicit CPU/mirror/resident value to round-trip.
            public SimpleDdgiSchedulerMode? SimpleDdgiSchedulerMode { get; init; }
            public int? SimpleDdgiSchedulerReentryStableFrameCount { get; init; }
            public SimpleDdgiProbeResidencyMode? SimpleDdgiProbeResidencyMode { get; init; }
            public int? SimpleDdgiSparsePhysicalPageBudget { get; init; }
            public int? SimpleDdgiSparseMinimumPhysicalPageBudget { get; init; }
            public int? SimpleDdgiSparseRetentionFrames { get; init; }
            public int? SimpleDdgiSparseMaximumAdmissionsPerFrame { get; init; }
            public int? SimpleDdgiSparseMaximumReceiverFeedbackRequests { get; init; }
            public int? SimpleDdgiSparseInactiveRetryFrames { get; init; }
            public SimpleDdgiAuthoredVolumeFile[] SimpleDdgiAuthoredVolumes { get; init; } = Array.Empty<SimpleDdgiAuthoredVolumeFile>();
            public bool SimpleDdgiSharedMemoryBlendEnabled { get; init; } = true;
            public bool SimpleDdgiClassificationSchedulingEnabled { get; init; } = true;
            public bool SimpleDdgiCostAwareSchedulingEnabled { get; init; } = true;
            public bool SimpleDdgiReceiverContributionFeedbackEnabled { get; init; } = true;
            public bool SimpleDdgiPersistentWarmStartEnabled { get; init; } = true;
            public bool SimpleDdgiSparseResidualPropagationEnabled { get; init; } = true;
            public bool SimpleDdgiUrgentRelightEnabled { get; init; } = true;
            public int? SimpleDdgiUrgentRelightProbeBudget { get; init; }
            public SimpleDdgiSourceCacheLayoutMode SimpleDdgiSourceCacheLayoutMode { get; init; } =
                SimpleDdgiSourceCacheLayoutMode.FixedRecord;
            public bool SimpleDdgiClassificationReadbackEnabled { get; init; } = true;
            public bool SimpleDdgiAdaptiveHysteresisEnabled { get; init; } = true;
            public bool SimpleDdgiLightingDirtyBoostEnabled { get; init; } = true;
            public bool SimpleDdgiDynamicGeometryDirtyBoostEnabled { get; init; } = true;
            public bool SimpleDdgiAdaptiveRaysEnabled { get; init; } = true;
            public bool SimpleDdgiTransportV2Enabled { get; init; } = true;
            public bool SimpleDdgiThinSurfaceTransmissionEnabled { get; init; }
            public bool SimpleDdgiAutomaticProbeDensityEnabled { get; init; } = true;
            public bool SimpleDdgiStructuredGatherEnabled { get; init; } = true;
            public SimpleDdgiReceiverCacheMode? SimpleDdgiReceiverCacheMode
            {
                get;
                init;
            }
            public SimpleDdgiLayoutAdmissionMode SimpleDdgiLayoutAdmissionMode { get; init; } = SimpleDdgiLayoutAdmissionMode.Degrade;
            public bool SimpleDdgiReducedBlendEnabled { get; init; }
            // Nullable representation controls preserve the selected quality
            // tier's production defaults when loading files written before
            // these fields existed. Explicit rollback choices still round-trip.
            public bool? SimpleDdgiSampledAtlasEnabled { get; init; }
            public SimpleDdgiSampledAtlasCoverageMode? SimpleDdgiSampledAtlasCoverageMode { get; init; }
            public SimpleDdgiStoragePackingMode? SimpleDdgiStoragePackingMode { get; init; }
            public float SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold { get; init; } = 1.0f;
            public bool SimpleDdgiToroidalScrollingEnabled { get; init; } = true;
            public bool SimpleDdgiRegionalInvalidationEnabled { get; init; } = true;
            public bool SimpleDdgiMutationJournalEnabled { get; init; } = true;
            public bool SimpleDdgiMutationJournalValidationOracleEnabled { get; init; }
            public bool SimpleDdgiFogEnabled { get; init; } = true;
            public bool? SimpleDdgiDirectionalFogEnabled { get; init; }
            public bool? DdgiOpacityMicromapExperimentEnabled { get; init; }
            public bool? DdgiRayTracingPipelineExperimentEnabled { get; init; }
            public bool? SimpleDdgiDirectionalRayGuidingExperimentEnabled { get; init; }
            public bool? DdgiTaggedCausticCacheExperimentEnabled { get; init; }
            public bool? SimpleDdgiNearFieldResidualExperimentEnabled { get; init; }
            // Version-10 authoritative modes.  Nullable fields preserve the
            // schema-v9 boolean aliases when an older file is loaded.
            public SimpleDdgiReceiverFeedbackMode? SimpleDdgiReceiverFeedbackMode { get; init; }
            public DdgiOpacityMicromapMode? DdgiOpacityMicromapMode { get; init; }
            public SimpleDdgiDirectionalGuidingMode? SimpleDdgiDirectionalGuidingMode { get; init; }
            public GiCausticMode? GiCausticMode { get; init; }
            public SimpleDdgiNearFieldResidualMode? SimpleDdgiNearFieldResidualMode { get; init; }
            public SimpleDdgiNearFieldResidualQualityPreset?
                SimpleDdgiNearFieldResidualQualityPreset { get; init; }
            public bool? SimpleDdgiNearFieldResidualAdvancedOverridesEnabled { get; init; }
            public bool? SimpleDdgiNearFieldResidualLocalAdaptiveSchedulingEnabled
                { get; init; }
            public float? SimpleDdgiNearFieldResidualMaximumTraceDistanceMeters { get; init; }
            public int? SimpleDdgiNearFieldResidualRaysPerPixel { get; init; }
            public int? SimpleDdgiNearFieldResidualFilterIterationCount { get; init; }
            public float? SimpleDdgiNearFieldResidualIntensity { get; init; }
            public string? SimpleDdgiReceiverFeedbackQualificationId { get; init; }
            public string? DdgiOpacityMicromapQualificationId { get; init; }
            public string? SimpleDdgiDirectionalGuidingQualificationId { get; init; }
            public string? GiCausticQualificationId { get; init; }
            public string? SimpleDdgiNearFieldResidualQualificationId { get; init; }
            public bool SimpleDdgiParticlesEnabled { get; init; } = true;
            // Version-8 compatibility alias. Version 9 writes it for one schema
            // alongside the authoritative typed modes, then reads it only when
            // loading an older schema.
            public bool SimpleDdgiRoughSpecularEnabled { get; init; }
            public SimpleDdgiLocalLightSamplingMode? SimpleDdgiLocalLightSamplingMode { get; init; }
            public SimpleDdgiDirectionalRadianceMode? SimpleDdgiDirectionalRadianceMode { get; init; }
            public SimpleDdgiGlossyTransportMode? SimpleDdgiGlossyTransportMode { get; init; }
            public DdgiSkinnedGeometryMode? DdgiSkinnedGeometryMode { get; init; }
            public DdgiTransparentGeometryMode? DdgiTransparentGeometryMode { get; init; }
            public DdgiFoliageGeometryMode? DdgiFoliageGeometryMode { get; init; }
            public int? SimpleDdgiNearLocalLightSamplesPerHit { get; init; }
            public int? SimpleDdgiMidLocalLightSamplesPerHit { get; init; }
            public int? SimpleDdgiFarLocalLightSamplesPerHit { get; init; }
            public int? SimpleDdgiExactLocalLightThreshold { get; init; }
            public float? SimpleDdgiLightTreeUniformMixtureProbability { get; init; }
            public int? SimpleDdgiLightTreeMaximumRefitAge { get; init; }
            public ulong? DdgiDynamicBlasMemoryBudgetBytes { get; init; }
            public ulong? DdgiDynamicBlasScratchBudgetBytes { get; init; }
            public int? DdgiDynamicBlasBuildsPerFrame { get; init; }
            public int? DdgiDynamicBlasPrimitivesPerFrame { get; init; }
            public int? DdgiFoliageProxyTriangleBudget { get; init; }
            public int? DdgiFoliageProxyUpdateCadenceFrames { get; init; }
            public int? DdgiTransparencyCandidateLimit { get; init; }
            public int? DdgiTransparencyLayerLimit { get; init; }
            public int? DdgiDecalCandidateLimit { get; init; }
            public ulong? SimpleDdgiDirectionalRadianceMemoryBudgetBytes { get; init; }
            public float? SimpleDdgiRoughSpecularMinimumRoughness { get; init; }
            public float? SimpleDdgiRoughSpecularFullWeightRoughness { get; init; }
            public float SimpleDdgiProbeSpacing { get; init; } = 1.25f;
            public int SimpleDdgiRingCount { get; init; } = 3;
            public float SimpleDdgiRingBaseSpacing { get; init; } = 1.25f;
            public float SimpleDdgiRingSpacingMultiplier { get; init; } = 3.0f;
            public float SimpleDdgiViewForwardPlacementFraction { get; init; } = 0.6f;
            public SimpleDdgiVerticalRingPolicy SimpleDdgiVerticalRingPolicy { get; init; } = SimpleDdgiVerticalRingPolicy.CameraRelativeWithHysteresis;
            public float SimpleDdgiReceiverVerticalAnchor { get; init; } = 4.5f;
            public float SimpleDdgiVerticalRecenterHysteresisFraction { get; init; } = 0.25f;
            public int SimpleDdgiNearRingGridSizeX { get; init; } = 28;
            public int SimpleDdgiNearRingGridSizeY { get; init; } = 14;
            public int SimpleDdgiNearRingGridSizeZ { get; init; } = 28;
            public int SimpleDdgiMidRingGridSizeX { get; init; } = 18;
            public int SimpleDdgiMidRingGridSizeY { get; init; } = 10;
            public int SimpleDdgiMidRingGridSizeZ { get; init; } = 18;
            public int SimpleDdgiFarRingGridSizeX { get; init; } = 12;
            public int SimpleDdgiFarRingGridSizeY { get; init; } = 8;
            public int SimpleDdgiFarRingGridSizeZ { get; init; } = 12;
            public bool? SimpleDdgiRefinementBricksEnabled { get; init; }
            public int? SimpleDdgiRefinementMaximumBricks { get; init; }
            public int? SimpleDdgiRefinementGridSizeX { get; init; }
            public int? SimpleDdgiRefinementGridSizeY { get; init; }
            public int? SimpleDdgiRefinementGridSizeZ { get; init; }
            public float? SimpleDdgiRefinementSpacingScale { get; init; }
            public int? SimpleDdgiRefinementRetentionFrames { get; init; }
            public float? SimpleDdgiRefinementMinimumEmissiveLuminanceNits { get; init; }
            public float? SimpleDdgiRefinementMaximumEmitterAreaSquareMeters { get; init; }
            public bool? SimpleDdgiNearVisibilitySidecarEnabled { get; init; }
            public ulong? SimpleDdgiNearVisibilitySidecarMemoryBudgetBytes { get; init; }
            public int SimpleDdgiRaysPerProbe { get; init; } = 96;
            public int SimpleDdgiMaintenanceRaysPerProbe { get; init; } = 24;
            public int SimpleDdgiNearFullRaysPerProbe { get; init; } = 64;
            public int SimpleDdgiMidFullRaysPerProbe { get; init; } = 48;
            public int SimpleDdgiFarFullRaysPerProbe { get; init; } = 24;
            public int SimpleDdgiNearMaintenanceRaysPerProbe { get; init; } = 24;
            public int SimpleDdgiMidMaintenanceRaysPerProbe { get; init; } = 12;
            public int SimpleDdgiFarMaintenanceRaysPerProbe { get; init; } = 6;
            public int SimpleDdgiNearMinimumUpdateQuota { get; init; } = 512;
            public int SimpleDdgiMidMinimumUpdateQuota { get; init; } = 96;
            public int SimpleDdgiFarMinimumUpdateQuota { get; init; } = 24;
            public int SimpleDdgiNearMaximumUpdateQuota { get; init; } = 1_024;
            public int SimpleDdgiMidMaximumUpdateQuota { get; init; } = 324;
            public int SimpleDdgiFarMaximumUpdateQuota { get; init; } = 128;
            public int SimpleDdgiNearMaterialTextureMaxCascade { get; init; } = 1;
            public int SimpleDdgiMidMaterialTextureMaxCascade { get; init; } = 0;
            public int SimpleDdgiFarMaterialTextureMaxCascade { get; init; } = -1;
            public int SimpleDdgiNearMaxShadedLights { get; init; } = 8;
            public int SimpleDdgiMidMaxShadedLights { get; init; } = 4;
            public int SimpleDdgiFarMaxShadedLights { get; init; } = 2;
            public float SimpleDdgiHysteresis { get; init; } = 0.97f;
            public float SimpleDdgiHysteresisChangeThreshold { get; init; } = 0.50f;
            public float SimpleDdgiHysteresisStepThreshold { get; init; } = 0.80f;
            public int SimpleDdgiLightingDirtyFrameCount { get; init; } = 30;
            public int SimpleDdgiStableMaintenanceUpdateCount { get; init; } = 3;
            public float SimpleDdgiStableMaintenanceEmaThreshold { get; init; } = 0.03f;
            public float SimpleDdgiTransportSolverRelaxation { get; init; } = 0.70f;
            public float SimpleDdgiTransportAlbedoClamp { get; init; } = 0.95f;
            public float? SimpleDdgiTransportTailRelativeTolerance { get; init; }
            public int? SimpleDdgiTransportAcceleratedSweepCount { get; init; }
            public bool? SimpleDdgiTransportAccelerationEnabled { get; init; }
            public bool? SimpleDdgiTransportTailCertificationEnabled { get; init; }
            // These two names are deliberately ignored on write. Unknown legacy
            // fields are retained in ExtensionData and read by ApplyTo below so
            // old files migrate without keeping obsolete authority in new files.
            public int SimpleDdgiTransportSourceRefreshFrames { get; init; } = 2_048;
            public float SimpleDdgiAutomaticProbeDensityScale { get; init; } = 0.70f;
            // These are spacing-relative authoring controls used by the shared
            // forward gather.  Persist them with the layout so a capture or
            // reload cannot silently change interpolation/visibility behavior.
            public float SimpleDdgiNormalBias { get; init; } = 0.1f;
            public float SimpleDdgiViewBias { get; init; } = 0.3f;
            public float SimpleDdgiMaximumWorldBiasMeters { get; init; } = 0.20f;
            public float SimpleDdgiArchitecturalThicknessMeters { get; init; } = 0.80f;
            public int SimpleDdgiProbeUpdatesPerFrame { get; init; } = 2_048;
            public bool FarFieldClipmapEnabled { get; init; } = true;
            public bool FarFieldPagedEnabled { get; init; } = true;
            public bool GiFarFieldMaterialV2 { get; init; }
            public bool FarFieldSkyVisibilityEnabled { get; init; } = true;
            public bool FarFieldSunShadowEnabled { get; init; } = true;
            public int FarFieldClipmapResolution { get; init; } = 128;
            public float FarFieldStartDistance { get; init; } = 12.0f;
            public int FarFieldMaxTraceSteps { get; init; } = 256;
            public int FarFieldPageResolution { get; init; } = 32;
            public int FarFieldCascadeCount { get; init; } = 3;
            public int FarFieldResidentPageBudget { get; init; } = 48;
            public int FarFieldPageUpdatesPerFrame { get; init; } = 1;
            public int FarFieldPageRequestRadius { get; init; } = 1;
            public float FarFieldBaseVoxelSize { get; init; } = 1.0f;
            public float FarFieldCascadeVoxelScale { get; init; } = 3.0f;
            public ulong FarFieldMemoryBudgetBytes { get; init; } = 96UL * 1024UL * 1024UL;
            public bool FarFieldForceAll { get; init; }
            public bool StreamedGiAccelerationStructuresEnabled { get; init; } = true;
            public ulong GiAccelerationStructureMemoryBudgetBytes { get; init; } = 1024UL * 1024UL * 1024UL;
            public float GiAccelerationStructureStaticResidentDistance { get; init; } = 256.0f;
            public int GiAccelerationStructureMaximumStaticInstances { get; init; } = 8_192;
            public int GiAccelerationStructureEvictionGraceFrames { get; init; } = 120;
            public int DdgiProbeUpdatePrimaryRayBudget { get; init; } = GlobalIlluminationSettings.DefaultDdgiProbeUpdatePrimaryRayBudget;
            public int DdgiMaxShadedLights { get; init; } = 8;
            public int DdgiMaterialTextureMaxCascade { get; init; } = 1;
            public ulong DdgiAtlasMemoryBudgetBytes { get; init; } = GlobalIlluminationSettings.DefaultDdgiAtlasMemoryBudgetBytes;
            public float DdgiThinWallLeakClampStrength { get; init; } = 0.9f;
            public float DdgiSelfShadowBiasScale { get; init; } = 1.0f;
            public float ResolutionScale { get; init; } = 0.5f;
            public float MaxBounceDistance { get; init; } = 6.0f;
            public bool TemporalEnabled { get; init; } = true;
            public bool DenoiserEnabled { get; init; } = true;
            public float HistoryResponsiveness { get; init; } = 0.18f;
            public float NormalRejectionThreshold { get; init; } = 0.85f;
            public float DepthRejectionThreshold { get; init; } = 0.08f;
            public float LeakClampStrength { get; init; } = 0.75f;

            [JsonExtensionData]
            public Dictionary<string, JsonElement>? ExtensionData { get; init; }

            public static GlobalIlluminationFile FromSettings(GlobalIlluminationSettings settings)
            {
                return new GlobalIlluminationFile
                {
                    Enabled = settings.Enabled,
                    Mode = settings.Mode,
                    DebugView = settings.DebugView,
                    EmergencyGiFallbackEnabled = settings.EmergencyGiFallbackEnabled,
                    IndirectIntensity = settings.IndirectIntensity,
                    EnvironmentFallbackIntensity = settings.EnvironmentFallbackIntensity,
                    UseDdgi = settings.UseDdgi,
                    UseRayQueryBackend = settings.UseRayQueryBackend,
                    DdgiQualityTier = settings.DdgiQualityTier,
                    GiMaterialTransportV2 = settings.GiMaterialTransportV2,
                    GiEmissiveMeshSampling = settings.GiEmissiveMeshSampling,
                    DdgiProbeClassificationEnabled = settings.DdgiProbeClassificationEnabled,
                    DdgiProbeRelocationEnabled = settings.DdgiProbeRelocationEnabled,
                    DdgiProbeL1MetadataEnabled = settings.DdgiProbeL1MetadataEnabled,
                    DdgiCameraRelativeEnabled = settings.DdgiCameraRelativeEnabled,
                    DdgiAdaptiveBudgetingEnabled = settings.DdgiAdaptiveBudgetingEnabled,
                    DdgiThinWallPolicyEnabled = settings.DdgiThinWallPolicyEnabled,
                    DdgiAlphaMaskedTransportEnabled = settings.DdgiAlphaMaskedTransportEnabled,
                    SimpleDdgiSchedulerMode = settings.SimpleDdgiSchedulerMode.Sanitize(),
                    SimpleDdgiSchedulerReentryStableFrameCount =
                        settings.SimpleDdgiSchedulerReentryStableFrameCount,
                    SimpleDdgiProbeResidencyMode =
                        settings.SimpleDdgiProbeResidencyMode.Sanitize(),
                    SimpleDdgiSparsePhysicalPageBudget =
                        settings.SimpleDdgiSparsePhysicalPageBudget,
                    SimpleDdgiSparseMinimumPhysicalPageBudget =
                        settings.SimpleDdgiSparseMinimumPhysicalPageBudget,
                    SimpleDdgiSparseRetentionFrames =
                        settings.SimpleDdgiSparseRetentionFrames,
                    SimpleDdgiSparseMaximumAdmissionsPerFrame =
                        settings.SimpleDdgiSparseMaximumAdmissionsPerFrame,
                    SimpleDdgiSparseMaximumReceiverFeedbackRequests =
                        settings.SimpleDdgiSparseMaximumReceiverFeedbackRequests,
                    SimpleDdgiSparseInactiveRetryFrames =
                        settings.SimpleDdgiSparseInactiveRetryFrames,
                    SimpleDdgiAuthoredVolumes = settings.SimpleDdgiAuthoredVolumes
                        .Take(GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount)
                        .Select(SimpleDdgiAuthoredVolumeFile.FromSettings)
                        .ToArray(),
                    SimpleDdgiSharedMemoryBlendEnabled = settings.SimpleDdgiSharedMemoryBlendEnabled,
                    SimpleDdgiClassificationSchedulingEnabled = settings.SimpleDdgiClassificationSchedulingEnabled,
                    SimpleDdgiCostAwareSchedulingEnabled = settings.SimpleDdgiCostAwareSchedulingEnabled,
                    SimpleDdgiReceiverContributionFeedbackEnabled =
                        settings.SimpleDdgiReceiverContributionFeedbackEnabled,
                    SimpleDdgiPersistentWarmStartEnabled =
                        settings.SimpleDdgiPersistentWarmStartEnabled,
                    SimpleDdgiSparseResidualPropagationEnabled =
                        settings.SimpleDdgiSparseResidualPropagationEnabled,
                    SimpleDdgiUrgentRelightEnabled =
                        settings.SimpleDdgiUrgentRelightEnabled,
                    SimpleDdgiUrgentRelightProbeBudget =
                        settings.SimpleDdgiUrgentRelightProbeBudget,
                    SimpleDdgiSourceCacheLayoutMode =
                        settings.SimpleDdgiSourceCacheLayoutMode.Sanitize(),
                    SimpleDdgiClassificationReadbackEnabled = settings.SimpleDdgiClassificationReadbackEnabled,
                    SimpleDdgiAdaptiveHysteresisEnabled = settings.SimpleDdgiAdaptiveHysteresisEnabled,
                    SimpleDdgiLightingDirtyBoostEnabled = settings.SimpleDdgiLightingDirtyBoostEnabled,
                    SimpleDdgiDynamicGeometryDirtyBoostEnabled = settings.SimpleDdgiDynamicGeometryDirtyBoostEnabled,
                    SimpleDdgiAdaptiveRaysEnabled = settings.SimpleDdgiAdaptiveRaysEnabled,
                    SimpleDdgiTransportV2Enabled = settings.SimpleDdgiTransportV2Enabled,
                    SimpleDdgiThinSurfaceTransmissionEnabled = settings.SimpleDdgiThinSurfaceTransmissionEnabled,
                    SimpleDdgiAutomaticProbeDensityEnabled = settings.SimpleDdgiAutomaticProbeDensityEnabled,
                    SimpleDdgiStructuredGatherEnabled = settings.SimpleDdgiStructuredGatherEnabled,
                    SimpleDdgiReceiverCacheMode =
                        settings.SimpleDdgiReceiverCacheMode,
                    SimpleDdgiLayoutAdmissionMode = settings.SimpleDdgiLayoutAdmissionMode,
                    SimpleDdgiReducedBlendEnabled = settings.SimpleDdgiReducedBlendEnabled,
                    SimpleDdgiSampledAtlasEnabled = settings.SimpleDdgiSampledAtlasEnabled,
                    SimpleDdgiSampledAtlasCoverageMode = settings.SimpleDdgiSampledAtlasCoverageMode,
                    SimpleDdgiStoragePackingMode = settings.SimpleDdgiStoragePackingMode,
                    SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold = settings.SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold,
                    SimpleDdgiToroidalScrollingEnabled = settings.SimpleDdgiToroidalScrollingEnabled,
                    SimpleDdgiRegionalInvalidationEnabled = settings.SimpleDdgiRegionalInvalidationEnabled,
                    SimpleDdgiMutationJournalEnabled = settings.SimpleDdgiMutationJournalEnabled,
                    SimpleDdgiMutationJournalValidationOracleEnabled =
                        settings.SimpleDdgiMutationJournalValidationOracleEnabled,
                    SimpleDdgiFogEnabled = settings.SimpleDdgiFogEnabled,
                    SimpleDdgiDirectionalFogEnabled =
                        settings.SimpleDdgiDirectionalFogEnabled,
                    DdgiOpacityMicromapExperimentEnabled =
                        settings.DdgiOpacityMicromapExperimentEnabled,
                    DdgiRayTracingPipelineExperimentEnabled =
                        settings.DdgiRayTracingPipelineExperimentEnabled,
                    SimpleDdgiDirectionalRayGuidingExperimentEnabled =
                        settings.SimpleDdgiDirectionalRayGuidingExperimentEnabled,
                    DdgiTaggedCausticCacheExperimentEnabled =
                        settings.DdgiTaggedCausticCacheExperimentEnabled,
                    SimpleDdgiNearFieldResidualExperimentEnabled =
                        settings.SimpleDdgiNearFieldResidualExperimentEnabled,
                    SimpleDdgiReceiverFeedbackMode =
                        settings.SimpleDdgiReceiverFeedbackMode,
                    DdgiOpacityMicromapMode = settings.DdgiOpacityMicromapMode,
                    SimpleDdgiDirectionalGuidingMode =
                        settings.SimpleDdgiDirectionalGuidingMode,
                    GiCausticMode = settings.GiCausticMode,
                    SimpleDdgiNearFieldResidualMode =
                        settings.SimpleDdgiNearFieldResidualMode,
                    SimpleDdgiNearFieldResidualQualityPreset =
                        settings.SimpleDdgiNearFieldResidualQualityPreset,
                    SimpleDdgiNearFieldResidualAdvancedOverridesEnabled =
                        settings.SimpleDdgiNearFieldResidualAdvancedOverridesEnabled,
                    SimpleDdgiNearFieldResidualLocalAdaptiveSchedulingEnabled =
                        settings.SimpleDdgiNearFieldResidualLocalAdaptiveSchedulingEnabled,
                    SimpleDdgiNearFieldResidualMaximumTraceDistanceMeters =
                        settings.SimpleDdgiNearFieldResidualMaximumTraceDistanceMeters,
                    SimpleDdgiNearFieldResidualRaysPerPixel =
                        settings.SimpleDdgiNearFieldResidualRaysPerPixel,
                    SimpleDdgiNearFieldResidualFilterIterationCount =
                        settings.SimpleDdgiNearFieldResidualFilterIterationCount,
                    SimpleDdgiNearFieldResidualIntensity =
                        settings.SimpleDdgiNearFieldResidualIntensity,
                    SimpleDdgiReceiverFeedbackQualificationId =
                        settings.SimpleDdgiReceiverFeedbackQualificationId,
                    DdgiOpacityMicromapQualificationId =
                        settings.DdgiOpacityMicromapQualificationId,
                    SimpleDdgiDirectionalGuidingQualificationId =
                        settings.SimpleDdgiDirectionalGuidingQualificationId,
                    GiCausticQualificationId = settings.GiCausticQualificationId,
                    SimpleDdgiNearFieldResidualQualificationId =
                        settings.SimpleDdgiNearFieldResidualQualificationId,
                    SimpleDdgiParticlesEnabled = settings.SimpleDdgiParticlesEnabled,
                    SimpleDdgiRoughSpecularEnabled = settings.SimpleDdgiRoughSpecularEnabled,
                    SimpleDdgiLocalLightSamplingMode = settings.SimpleDdgiLocalLightSamplingMode,
                    SimpleDdgiDirectionalRadianceMode = settings.SimpleDdgiDirectionalRadianceMode,
                    SimpleDdgiGlossyTransportMode = settings.SimpleDdgiGlossyTransportMode,
                    DdgiSkinnedGeometryMode = settings.DdgiSkinnedGeometryMode,
                    DdgiTransparentGeometryMode = settings.DdgiTransparentGeometryMode,
                    DdgiFoliageGeometryMode = settings.DdgiFoliageGeometryMode,
                    SimpleDdgiNearLocalLightSamplesPerHit =
                        settings.SimpleDdgiNearLocalLightSamplesPerHit,
                    SimpleDdgiMidLocalLightSamplesPerHit =
                        settings.SimpleDdgiMidLocalLightSamplesPerHit,
                    SimpleDdgiFarLocalLightSamplesPerHit =
                        settings.SimpleDdgiFarLocalLightSamplesPerHit,
                    SimpleDdgiExactLocalLightThreshold = settings.SimpleDdgiExactLocalLightThreshold,
                    SimpleDdgiLightTreeUniformMixtureProbability =
                        settings.SimpleDdgiLightTreeUniformMixtureProbability,
                    SimpleDdgiLightTreeMaximumRefitAge = settings.SimpleDdgiLightTreeMaximumRefitAge,
                    DdgiDynamicBlasMemoryBudgetBytes = settings.DdgiDynamicBlasMemoryBudgetBytes,
                    DdgiDynamicBlasScratchBudgetBytes = settings.DdgiDynamicBlasScratchBudgetBytes,
                    DdgiDynamicBlasBuildsPerFrame = settings.DdgiDynamicBlasBuildsPerFrame,
                    DdgiDynamicBlasPrimitivesPerFrame = settings.DdgiDynamicBlasPrimitivesPerFrame,
                    DdgiFoliageProxyTriangleBudget = settings.DdgiFoliageProxyTriangleBudget,
                    DdgiFoliageProxyUpdateCadenceFrames = settings.DdgiFoliageProxyUpdateCadenceFrames,
                    DdgiTransparencyCandidateLimit = settings.DdgiTransparencyCandidateLimit,
                    DdgiTransparencyLayerLimit = settings.DdgiTransparencyLayerLimit,
                    DdgiDecalCandidateLimit = settings.DdgiDecalCandidateLimit,
                    SimpleDdgiDirectionalRadianceMemoryBudgetBytes =
                        settings.SimpleDdgiDirectionalRadianceMemoryBudgetBytes,
                    SimpleDdgiRoughSpecularMinimumRoughness =
                        settings.SimpleDdgiRoughSpecularMinimumRoughness,
                    SimpleDdgiRoughSpecularFullWeightRoughness =
                        settings.SimpleDdgiRoughSpecularFullWeightRoughness,
                    SimpleDdgiProbeSpacing = settings.SimpleDdgiProbeSpacing,
                    SimpleDdgiRingCount = settings.SimpleDdgiRingCount,
                    SimpleDdgiRingBaseSpacing = settings.SimpleDdgiRingBaseSpacing,
                    SimpleDdgiRingSpacingMultiplier = settings.SimpleDdgiRingSpacingMultiplier,
                    SimpleDdgiViewForwardPlacementFraction = settings.SimpleDdgiViewForwardPlacementFraction,
                    SimpleDdgiVerticalRingPolicy = settings.SimpleDdgiVerticalRingPolicy,
                    SimpleDdgiReceiverVerticalAnchor = settings.SimpleDdgiReceiverVerticalAnchor,
                    SimpleDdgiVerticalRecenterHysteresisFraction = settings.SimpleDdgiVerticalRecenterHysteresisFraction,
                    SimpleDdgiNearRingGridSizeX = settings.SimpleDdgiNearRingGridSizeX,
                    SimpleDdgiNearRingGridSizeY = settings.SimpleDdgiNearRingGridSizeY,
                    SimpleDdgiNearRingGridSizeZ = settings.SimpleDdgiNearRingGridSizeZ,
                    SimpleDdgiMidRingGridSizeX = settings.SimpleDdgiMidRingGridSizeX,
                    SimpleDdgiMidRingGridSizeY = settings.SimpleDdgiMidRingGridSizeY,
                    SimpleDdgiMidRingGridSizeZ = settings.SimpleDdgiMidRingGridSizeZ,
                    SimpleDdgiFarRingGridSizeX = settings.SimpleDdgiFarRingGridSizeX,
                    SimpleDdgiFarRingGridSizeY = settings.SimpleDdgiFarRingGridSizeY,
                    SimpleDdgiFarRingGridSizeZ = settings.SimpleDdgiFarRingGridSizeZ,
                    SimpleDdgiRefinementBricksEnabled = settings.SimpleDdgiRefinementBricksEnabled,
                    SimpleDdgiRefinementMaximumBricks = settings.SimpleDdgiRefinementMaximumBricks,
                    SimpleDdgiRefinementGridSizeX = settings.SimpleDdgiRefinementGridSizeX,
                    SimpleDdgiRefinementGridSizeY = settings.SimpleDdgiRefinementGridSizeY,
                    SimpleDdgiRefinementGridSizeZ = settings.SimpleDdgiRefinementGridSizeZ,
                    SimpleDdgiRefinementSpacingScale = settings.SimpleDdgiRefinementSpacingScale,
                    SimpleDdgiRefinementRetentionFrames = settings.SimpleDdgiRefinementRetentionFrames,
                    SimpleDdgiRefinementMinimumEmissiveLuminanceNits =
                        settings.SimpleDdgiRefinementMinimumEmissiveLuminanceNits,
                    SimpleDdgiRefinementMaximumEmitterAreaSquareMeters =
                        settings.SimpleDdgiRefinementMaximumEmitterAreaSquareMeters,
                    SimpleDdgiNearVisibilitySidecarEnabled =
                        settings.SimpleDdgiNearVisibilitySidecarEnabled,
                    SimpleDdgiNearVisibilitySidecarMemoryBudgetBytes =
                        settings.SimpleDdgiNearVisibilitySidecarMemoryBudgetBytes,
                    SimpleDdgiRaysPerProbe = settings.SimpleDdgiRaysPerProbe,
                    SimpleDdgiMaintenanceRaysPerProbe = settings.SimpleDdgiMaintenanceRaysPerProbe,
                    SimpleDdgiNearFullRaysPerProbe = settings.SimpleDdgiNearFullRaysPerProbe,
                    SimpleDdgiMidFullRaysPerProbe = settings.SimpleDdgiMidFullRaysPerProbe,
                    SimpleDdgiFarFullRaysPerProbe = settings.SimpleDdgiFarFullRaysPerProbe,
                    SimpleDdgiNearMaintenanceRaysPerProbe = settings.SimpleDdgiNearMaintenanceRaysPerProbe,
                    SimpleDdgiMidMaintenanceRaysPerProbe = settings.SimpleDdgiMidMaintenanceRaysPerProbe,
                    SimpleDdgiFarMaintenanceRaysPerProbe = settings.SimpleDdgiFarMaintenanceRaysPerProbe,
                    SimpleDdgiNearMinimumUpdateQuota = settings.SimpleDdgiNearMinimumUpdateQuota,
                    SimpleDdgiMidMinimumUpdateQuota = settings.SimpleDdgiMidMinimumUpdateQuota,
                    SimpleDdgiFarMinimumUpdateQuota = settings.SimpleDdgiFarMinimumUpdateQuota,
                    SimpleDdgiNearMaximumUpdateQuota = settings.SimpleDdgiNearMaximumUpdateQuota,
                    SimpleDdgiMidMaximumUpdateQuota = settings.SimpleDdgiMidMaximumUpdateQuota,
                    SimpleDdgiFarMaximumUpdateQuota = settings.SimpleDdgiFarMaximumUpdateQuota,
                    SimpleDdgiNearMaterialTextureMaxCascade = settings.SimpleDdgiNearMaterialTextureMaxCascade,
                    SimpleDdgiMidMaterialTextureMaxCascade = settings.SimpleDdgiMidMaterialTextureMaxCascade,
                    SimpleDdgiFarMaterialTextureMaxCascade = settings.SimpleDdgiFarMaterialTextureMaxCascade,
                    SimpleDdgiNearMaxShadedLights = settings.SimpleDdgiNearMaxShadedLights,
                    SimpleDdgiMidMaxShadedLights = settings.SimpleDdgiMidMaxShadedLights,
                    SimpleDdgiFarMaxShadedLights = settings.SimpleDdgiFarMaxShadedLights,
                    SimpleDdgiHysteresis = settings.SimpleDdgiHysteresis,
                    SimpleDdgiHysteresisChangeThreshold = settings.SimpleDdgiHysteresisChangeThreshold,
                    SimpleDdgiHysteresisStepThreshold = settings.SimpleDdgiHysteresisStepThreshold,
                    SimpleDdgiLightingDirtyFrameCount = settings.SimpleDdgiLightingDirtyFrameCount,
                    SimpleDdgiStableMaintenanceUpdateCount = settings.SimpleDdgiStableMaintenanceUpdateCount,
                    SimpleDdgiStableMaintenanceEmaThreshold = settings.SimpleDdgiStableMaintenanceEmaThreshold,
                    SimpleDdgiTransportSolverRelaxation = settings.SimpleDdgiTransportSolverRelaxation,
                    SimpleDdgiTransportAlbedoClamp = settings.SimpleDdgiTransportAlbedoClamp,
                    SimpleDdgiTransportTailRelativeTolerance = settings.SimpleDdgiTransportTailRelativeTolerance,
                    SimpleDdgiTransportAcceleratedSweepCount = settings.SimpleDdgiTransportAcceleratedSweepCount,
                    SimpleDdgiTransportAccelerationEnabled = settings.SimpleDdgiTransportAccelerationEnabled,
                    SimpleDdgiTransportTailCertificationEnabled = settings.SimpleDdgiTransportTailCertificationEnabled,
                    SimpleDdgiTransportSourceRefreshFrames = settings.SimpleDdgiTransportSourceRefreshFrames,
                    SimpleDdgiAutomaticProbeDensityScale = settings.SimpleDdgiAutomaticProbeDensityScale,
                    SimpleDdgiNormalBias = settings.SimpleDdgiNormalBias,
                    SimpleDdgiViewBias = settings.SimpleDdgiViewBias,
                    SimpleDdgiMaximumWorldBiasMeters = settings.SimpleDdgiMaximumWorldBiasMeters,
                    SimpleDdgiArchitecturalThicknessMeters = settings.SimpleDdgiArchitecturalThicknessMeters,
                    SimpleDdgiProbeUpdatesPerFrame = settings.SimpleDdgiProbeUpdatesPerFrame,
                    FarFieldClipmapEnabled = settings.FarFieldClipmapEnabled,
                    FarFieldPagedEnabled = settings.FarFieldPagedEnabled,
                    GiFarFieldMaterialV2 = settings.GiFarFieldMaterialV2,
                    FarFieldSkyVisibilityEnabled = settings.FarFieldSkyVisibilityEnabled,
                    FarFieldSunShadowEnabled = settings.FarFieldSunShadowEnabled,
                    FarFieldClipmapResolution = settings.FarFieldClipmapResolution,
                    FarFieldStartDistance = settings.FarFieldStartDistance,
                    FarFieldMaxTraceSteps = settings.FarFieldMaxTraceSteps,
                    FarFieldPageResolution = settings.FarFieldPageResolution,
                    FarFieldCascadeCount = settings.FarFieldCascadeCount,
                    FarFieldResidentPageBudget = settings.FarFieldResidentPageBudget,
                    FarFieldPageUpdatesPerFrame = settings.FarFieldPageUpdatesPerFrame,
                    FarFieldPageRequestRadius = settings.FarFieldPageRequestRadius,
                    FarFieldBaseVoxelSize = settings.FarFieldBaseVoxelSize,
                    FarFieldCascadeVoxelScale = settings.FarFieldCascadeVoxelScale,
                    FarFieldMemoryBudgetBytes = settings.FarFieldMemoryBudgetBytes,
                    FarFieldForceAll = settings.FarFieldForceAll,
                    StreamedGiAccelerationStructuresEnabled = settings.StreamedGiAccelerationStructuresEnabled,
                    GiAccelerationStructureMemoryBudgetBytes = settings.GiAccelerationStructureMemoryBudgetBytes,
                    GiAccelerationStructureStaticResidentDistance = settings.GiAccelerationStructureStaticResidentDistance,
                    GiAccelerationStructureMaximumStaticInstances = settings.GiAccelerationStructureMaximumStaticInstances,
                    GiAccelerationStructureEvictionGraceFrames = settings.GiAccelerationStructureEvictionGraceFrames,
                    DdgiAsyncComputeEnabled = settings.DdgiAsyncComputeEnabled,
                    DdgiProbeUpdatePrimaryRayBudget = settings.DdgiProbeUpdatePrimaryRayBudget,
                    DdgiMaxShadedLights = settings.DdgiMaxShadedLights,
                    DdgiMaterialTextureMaxCascade = settings.DdgiMaterialTextureMaxCascade,
                    DdgiAtlasMemoryBudgetBytes = settings.DdgiAtlasMemoryBudgetBytes,
                    DdgiThinWallLeakClampStrength = settings.DdgiThinWallLeakClampStrength,
                    DdgiSelfShadowBiasScale = settings.DdgiSelfShadowBiasScale,
                    ResolutionScale = settings.ResolutionScale,
                    MaxBounceDistance = settings.MaxBounceDistance,
                    TemporalEnabled = settings.TemporalEnabled,
                    DenoiserEnabled = settings.DenoiserEnabled,
                    HistoryResponsiveness = settings.HistoryResponsiveness,
                    NormalRejectionThreshold = settings.NormalRejectionThreshold,
                    DepthRejectionThreshold = settings.DepthRejectionThreshold,
                    LeakClampStrength = settings.LeakClampStrength
                };
            }

            public void ApplyTo(GlobalIlluminationSettings settings, int sourceVersion)
            {
                settings.Enabled = Enabled;
                settings.Mode = Mode;
                settings.DebugView = DebugView;
                settings.EmergencyGiFallbackEnabled = EmergencyGiFallbackEnabled;
                settings.IndirectIntensity = IndirectIntensity;
                settings.EnvironmentFallbackIntensity = EnvironmentFallbackIntensity;
                settings.UseDdgi = UseDdgi;
                if (UseRayQueryBackend.HasValue)
                    settings.UseRayQueryBackend = UseRayQueryBackend.Value;
                settings.DdgiQualityTier = DdgiQualityTier;
                settings.GiMaterialTransportV2 = GiMaterialTransportV2;
                settings.GiEmissiveMeshSampling = GiEmissiveMeshSampling;
                settings.DdgiProbeClassificationEnabled = DdgiProbeClassificationEnabled;
                settings.DdgiProbeRelocationEnabled = DdgiProbeRelocationEnabled;
                settings.DdgiProbeL1MetadataEnabled = DdgiProbeL1MetadataEnabled;
                settings.DdgiCameraRelativeEnabled = DdgiCameraRelativeEnabled;
                settings.DdgiAdaptiveBudgetingEnabled = DdgiAdaptiveBudgetingEnabled;
                settings.DdgiThinWallPolicyEnabled = DdgiThinWallPolicyEnabled;
                settings.DdgiAlphaMaskedTransportEnabled = DdgiAlphaMaskedTransportEnabled;
                if (SimpleDdgiSchedulerMode.HasValue)
                {
                    settings.SimpleDdgiSchedulerMode =
                        SimpleDdgiSchedulerMode.Value.Sanitize();
                }
                if (SimpleDdgiSchedulerReentryStableFrameCount.HasValue)
                {
                    settings.SimpleDdgiSchedulerReentryStableFrameCount =
                        SimpleDdgiSchedulerReentryStableFrameCount.Value;
                }
                if (SimpleDdgiProbeResidencyMode.HasValue)
                {
                    settings.SimpleDdgiProbeResidencyMode =
                        SimpleDdgiProbeResidencyMode.Value.Sanitize();
                }
                if (SimpleDdgiSparsePhysicalPageBudget.HasValue)
                {
                    settings.SimpleDdgiSparsePhysicalPageBudget =
                        SimpleDdgiSparsePhysicalPageBudget.Value;
                }
                if (SimpleDdgiSparseMinimumPhysicalPageBudget.HasValue)
                {
                    settings.SimpleDdgiSparseMinimumPhysicalPageBudget =
                        SimpleDdgiSparseMinimumPhysicalPageBudget.Value;
                }
                if (SimpleDdgiSparseRetentionFrames.HasValue)
                    settings.SimpleDdgiSparseRetentionFrames = SimpleDdgiSparseRetentionFrames.Value;
                if (SimpleDdgiSparseMaximumAdmissionsPerFrame.HasValue)
                {
                    settings.SimpleDdgiSparseMaximumAdmissionsPerFrame =
                        SimpleDdgiSparseMaximumAdmissionsPerFrame.Value;
                }
                if (SimpleDdgiSparseMaximumReceiverFeedbackRequests.HasValue)
                {
                    settings.SimpleDdgiSparseMaximumReceiverFeedbackRequests =
                        SimpleDdgiSparseMaximumReceiverFeedbackRequests.Value;
                }
                if (SimpleDdgiSparseInactiveRetryFrames.HasValue)
                    settings.SimpleDdgiSparseInactiveRetryFrames = SimpleDdgiSparseInactiveRetryFrames.Value;
                settings.SimpleDdgiAuthoredVolumes.Clear();
                foreach (SimpleDdgiAuthoredVolumeFile? authoredVolume in
                    (SimpleDdgiAuthoredVolumes ?? Array.Empty<SimpleDdgiAuthoredVolumeFile>())
                    .Take(GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount))
                {
                    if (authoredVolume != null)
                        settings.SimpleDdgiAuthoredVolumes.Add(authoredVolume.ToSettings());
                }
                settings.SimpleDdgiSharedMemoryBlendEnabled = SimpleDdgiSharedMemoryBlendEnabled;
                settings.SimpleDdgiClassificationSchedulingEnabled = SimpleDdgiClassificationSchedulingEnabled;
                settings.SimpleDdgiCostAwareSchedulingEnabled = SimpleDdgiCostAwareSchedulingEnabled;
                settings.SimpleDdgiReceiverContributionFeedbackEnabled =
                    SimpleDdgiReceiverContributionFeedbackEnabled;
                settings.SimpleDdgiPersistentWarmStartEnabled =
                    SimpleDdgiPersistentWarmStartEnabled;
                settings.SimpleDdgiSparseResidualPropagationEnabled =
                    SimpleDdgiSparseResidualPropagationEnabled;
                settings.SimpleDdgiUrgentRelightEnabled =
                    SimpleDdgiUrgentRelightEnabled;
                if (SimpleDdgiUrgentRelightProbeBudget.HasValue)
                {
                    settings.SimpleDdgiUrgentRelightProbeBudget =
                        SimpleDdgiUrgentRelightProbeBudget.Value;
                }
                settings.SimpleDdgiSourceCacheLayoutMode =
                    SimpleDdgiSourceCacheLayoutMode.Sanitize();
                settings.SimpleDdgiClassificationReadbackEnabled = SimpleDdgiClassificationReadbackEnabled;
                settings.SimpleDdgiAdaptiveHysteresisEnabled = SimpleDdgiAdaptiveHysteresisEnabled;
                settings.SimpleDdgiLightingDirtyBoostEnabled = SimpleDdgiLightingDirtyBoostEnabled;
                settings.SimpleDdgiDynamicGeometryDirtyBoostEnabled = SimpleDdgiDynamicGeometryDirtyBoostEnabled;
                settings.SimpleDdgiAdaptiveRaysEnabled = SimpleDdgiAdaptiveRaysEnabled;
                settings.SimpleDdgiTransportV2Enabled = SimpleDdgiTransportV2Enabled;
                settings.SimpleDdgiThinSurfaceTransmissionEnabled = SimpleDdgiThinSurfaceTransmissionEnabled;
                settings.SimpleDdgiAutomaticProbeDensityEnabled = SimpleDdgiAutomaticProbeDensityEnabled;
                settings.SimpleDdgiStructuredGatherEnabled = SimpleDdgiStructuredGatherEnabled;
                if (SimpleDdgiReceiverCacheMode.HasValue &&
                    (sourceVersion >= 22 ||
                     SimpleDdgiReceiverCacheMode.Value.Sanitize() !=
                     global::Njulf.Rendering.Data.SimpleDdgiReceiverCacheMode.Exact))
                {
                    settings.SimpleDdgiReceiverCacheMode =
                        SimpleDdgiReceiverCacheMode.Value.Sanitize();
                }
                settings.SimpleDdgiLayoutAdmissionMode = SimpleDdgiLayoutAdmissionMode;
                settings.SimpleDdgiReducedBlendEnabled = SimpleDdgiReducedBlendEnabled;
                if (SimpleDdgiSampledAtlasEnabled.HasValue)
                {
                    settings.SimpleDdgiSampledAtlasEnabled =
                        SimpleDdgiSampledAtlasEnabled.Value;
                }
                if (SimpleDdgiSampledAtlasCoverageMode.HasValue)
                {
                    settings.SimpleDdgiSampledAtlasCoverageMode =
                        SimpleDdgiSampledAtlasCoverageMode.Value.Sanitize();
                }
                if (SimpleDdgiStoragePackingMode.HasValue)
                {
                    settings.SimpleDdgiStoragePackingMode =
                        SimpleDdgiStoragePackingMode.Value.Sanitize();
                }
                settings.SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold = SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold;
                settings.SimpleDdgiToroidalScrollingEnabled = SimpleDdgiToroidalScrollingEnabled;
                settings.SimpleDdgiRegionalInvalidationEnabled = SimpleDdgiRegionalInvalidationEnabled;
                settings.SimpleDdgiMutationJournalEnabled = SimpleDdgiMutationJournalEnabled;
                settings.SimpleDdgiMutationJournalValidationOracleEnabled =
                    SimpleDdgiMutationJournalValidationOracleEnabled;
                settings.SimpleDdgiFogEnabled = SimpleDdgiFogEnabled;
                if (SimpleDdgiDirectionalFogEnabled.HasValue)
                {
                    settings.SimpleDdgiDirectionalFogEnabled =
                        SimpleDdgiDirectionalFogEnabled.Value;
                }
                if (DdgiOpacityMicromapExperimentEnabled.HasValue)
                {
                    settings.DdgiOpacityMicromapExperimentEnabled =
                        DdgiOpacityMicromapExperimentEnabled.Value;
                }
                if (DdgiRayTracingPipelineExperimentEnabled.HasValue)
                {
                    settings.DdgiRayTracingPipelineExperimentEnabled =
                        DdgiRayTracingPipelineExperimentEnabled.Value;
                }
                if (SimpleDdgiDirectionalRayGuidingExperimentEnabled.HasValue)
                {
                    settings.SimpleDdgiDirectionalRayGuidingExperimentEnabled =
                        SimpleDdgiDirectionalRayGuidingExperimentEnabled.Value;
                }
                if (DdgiTaggedCausticCacheExperimentEnabled.HasValue)
                {
                    settings.DdgiTaggedCausticCacheExperimentEnabled =
                        DdgiTaggedCausticCacheExperimentEnabled.Value;
                }
                if (SimpleDdgiNearFieldResidualExperimentEnabled.HasValue)
                {
                    settings.SimpleDdgiNearFieldResidualExperimentEnabled =
                        SimpleDdgiNearFieldResidualExperimentEnabled.Value;
                }
                // New modes override their legacy aliases when present.  This
                // preserves an authored Off/AutoQualified request even though
                // schema-v10 also writes the old booleans for one-schema read
                // compatibility.
                if (SimpleDdgiReceiverFeedbackMode.HasValue)
                {
                    settings.SimpleDdgiReceiverFeedbackMode =
                        SimpleDdgiReceiverFeedbackMode.Value;
                }
                if (DdgiOpacityMicromapMode.HasValue)
                {
                    settings.DdgiOpacityMicromapMode = DdgiOpacityMicromapMode.Value;
                }
                if (SimpleDdgiDirectionalGuidingMode.HasValue)
                {
                    settings.SimpleDdgiDirectionalGuidingMode =
                        SimpleDdgiDirectionalGuidingMode.Value;
                }
                if (GiCausticMode.HasValue)
                    settings.GiCausticMode = GiCausticMode.Value;
                if (SimpleDdgiNearFieldResidualMode.HasValue)
                {
                    settings.SimpleDdgiNearFieldResidualMode =
                        SimpleDdgiNearFieldResidualMode.Value;
                }
                // AutoQualified was the schema-v14 rollout default, but without
                // installed device evidence it resolved to Off. Schema v15
                // intentionally upgrades that prior default to the bounded
                // explicit V2 path. Current-schema AutoQualified remains a
                // durable authored choice, and every other mode is preserved.
                if (sourceVersion < 15 &&
                    settings.SimpleDdgiNearFieldResidualMode ==
                        global::Njulf.Rendering.Resources
                            .SimpleDdgiNearFieldResidualMode.AutoQualified)
                {
                    settings.SimpleDdgiNearFieldResidualMode =
                        global::Njulf.Rendering.Resources
                            .SimpleDdgiNearFieldResidualMode.HiZAdaptive;
                }
                settings.SimpleDdgiNearFieldResidualQualityPreset =
                    SimpleDdgiNearFieldResidualQualityPreset ??
                    global::Njulf.Rendering.Resources
                        .SimpleDdgiNearFieldResidualQualityPreset.Balanced;
                if (SimpleDdgiNearFieldResidualAdvancedOverridesEnabled.HasValue)
                {
                    settings.SimpleDdgiNearFieldResidualAdvancedOverridesEnabled =
                        SimpleDdgiNearFieldResidualAdvancedOverridesEnabled.Value;
                }
                if (SimpleDdgiNearFieldResidualLocalAdaptiveSchedulingEnabled.HasValue &&
                    (sourceVersion >= 22 ||
                     SimpleDdgiNearFieldResidualLocalAdaptiveSchedulingEnabled.Value))
                {
                    settings.SimpleDdgiNearFieldResidualLocalAdaptiveSchedulingEnabled =
                        SimpleDdgiNearFieldResidualLocalAdaptiveSchedulingEnabled.Value;
                }
                if (SimpleDdgiNearFieldResidualMaximumTraceDistanceMeters.HasValue)
                {
                    settings.SimpleDdgiNearFieldResidualMaximumTraceDistanceMeters =
                        SimpleDdgiNearFieldResidualMaximumTraceDistanceMeters.Value;
                }
                if (SimpleDdgiNearFieldResidualRaysPerPixel.HasValue)
                {
                    settings.SimpleDdgiNearFieldResidualRaysPerPixel =
                        SimpleDdgiNearFieldResidualRaysPerPixel.Value;
                }
                if (SimpleDdgiNearFieldResidualFilterIterationCount.HasValue)
                {
                    settings.SimpleDdgiNearFieldResidualFilterIterationCount =
                        SimpleDdgiNearFieldResidualFilterIterationCount.Value;
                }
                if (SimpleDdgiNearFieldResidualIntensity.HasValue)
                {
                    settings.SimpleDdgiNearFieldResidualIntensity =
                        SimpleDdgiNearFieldResidualIntensity.Value;
                }
                if (SimpleDdgiReceiverFeedbackQualificationId != null)
                {
                    settings.SimpleDdgiReceiverFeedbackQualificationId =
                        SimpleDdgiReceiverFeedbackQualificationId;
                }
                if (DdgiOpacityMicromapQualificationId != null)
                {
                    settings.DdgiOpacityMicromapQualificationId =
                        DdgiOpacityMicromapQualificationId;
                }
                if (SimpleDdgiDirectionalGuidingQualificationId != null)
                {
                    settings.SimpleDdgiDirectionalGuidingQualificationId =
                        SimpleDdgiDirectionalGuidingQualificationId;
                }
                if (GiCausticQualificationId != null)
                    settings.GiCausticQualificationId = GiCausticQualificationId;
                // GPU/evidence/source semantics all changed for v14. Preserve
                // authored mode intent, but never carry a pre-v14 credential
                // into the V13/V6 admission path.
                if (sourceVersion >= 14 &&
                    SimpleDdgiNearFieldResidualQualificationId != null)
                {
                    settings.SimpleDdgiNearFieldResidualQualificationId =
                        SimpleDdgiNearFieldResidualQualificationId;
                }
                else if (sourceVersion < 14)
                {
                    settings.SimpleDdgiNearFieldResidualQualificationId = string.Empty;
                }
                settings.SimpleDdgiParticlesEnabled = SimpleDdgiParticlesEnabled;
                if (sourceVersion < 9)
                {
                    settings.SimpleDdgiLocalLightSamplingMode =
                        global::Njulf.Rendering.Data.SimpleDdgiLocalLightSamplingMode.LegacyTopKReference;
                    settings.SimpleDdgiNearLocalLightSamplesPerHit =
                        SimpleDdgiNearMaxShadedLights;
                    settings.SimpleDdgiMidLocalLightSamplesPerHit =
                        SimpleDdgiMidMaxShadedLights;
                    settings.SimpleDdgiFarLocalLightSamplesPerHit =
                        SimpleDdgiFarMaxShadedLights;
                    settings.SimpleDdgiExactLocalLightThreshold = 0;
                    settings.DdgiSkinnedGeometryMode =
                        global::Njulf.Rendering.Data.DdgiSkinnedGeometryMode.ConservativeProxy;
                    settings.DdgiTransparentGeometryMode =
                        global::Njulf.Rendering.Data.DdgiTransparentGeometryMode.MaskAndThin;
                    settings.DdgiFoliageGeometryMode =
                        global::Njulf.Rendering.Data.DdgiFoliageGeometryMode.Excluded;
                    settings.SimpleDdgiDirectionalRadianceMode =
                        SimpleDdgiRoughSpecularEnabled
                            ? global::Njulf.Rendering.Data.SimpleDdgiDirectionalRadianceMode.L2
                            : global::Njulf.Rendering.Data.SimpleDdgiDirectionalRadianceMode.Off;
                    settings.SimpleDdgiGlossyTransportMode =
                        SimpleDdgiRoughSpecularEnabled
                            ? global::Njulf.Rendering.Data.SimpleDdgiGlossyTransportMode.ReceiverOnly
                            : global::Njulf.Rendering.Data.SimpleDdgiGlossyTransportMode.Off;
                    settings.ContentDependentSettingsMigrationDiagnostic =
                        SimpleDdgiRoughSpecularEnabled
                            ? "Schema v8 rough-specular opt-in migrated explicitly to L2/ReceiverOnly; device qualification is still required."
                            : "Schema v8 DDGI content additions retained the qualified legacy/reference behavior and directional radiance remains Off.";
                }
                else
                {
                    if (SimpleDdgiLocalLightSamplingMode.HasValue)
                        settings.SimpleDdgiLocalLightSamplingMode = SimpleDdgiLocalLightSamplingMode.Value;
                    if (SimpleDdgiDirectionalRadianceMode.HasValue)
                        settings.SimpleDdgiDirectionalRadianceMode = SimpleDdgiDirectionalRadianceMode.Value;
                    if (SimpleDdgiGlossyTransportMode.HasValue)
                    {
                        SimpleDdgiGlossyTransportMode requestedGlossy =
                            SimpleDdgiGlossyTransportMode.Value;
                        if (sourceVersion < 12 && requestedGlossy ==
                            global::Njulf.Rendering.Data
                                .SimpleDdgiGlossyTransportMode.RecursiveCertified)
                        {
                            requestedGlossy = global::Njulf.Rendering.Data
                                .SimpleDdgiGlossyTransportMode.OneBounce;
                            settings.ContentDependentSettingsMigrationDiagnostic =
                                "Legacy RecursiveExperimental transport was not certified; migrated to OneBounce.";
                        }
                        settings.SimpleDdgiGlossyTransportMode = requestedGlossy;
                    }
                    if (DdgiSkinnedGeometryMode.HasValue)
                        settings.DdgiSkinnedGeometryMode = DdgiSkinnedGeometryMode.Value;
                    if (DdgiTransparentGeometryMode.HasValue)
                        settings.DdgiTransparentGeometryMode = DdgiTransparentGeometryMode.Value;
                    if (DdgiFoliageGeometryMode.HasValue)
                        settings.DdgiFoliageGeometryMode = DdgiFoliageGeometryMode.Value;
                    if (SimpleDdgiNearLocalLightSamplesPerHit.HasValue)
                    {
                        settings.SimpleDdgiNearLocalLightSamplesPerHit =
                            SimpleDdgiNearLocalLightSamplesPerHit.Value;
                    }
                    if (SimpleDdgiMidLocalLightSamplesPerHit.HasValue)
                    {
                        settings.SimpleDdgiMidLocalLightSamplesPerHit =
                            SimpleDdgiMidLocalLightSamplesPerHit.Value;
                    }
                    if (SimpleDdgiFarLocalLightSamplesPerHit.HasValue)
                    {
                        settings.SimpleDdgiFarLocalLightSamplesPerHit =
                            SimpleDdgiFarLocalLightSamplesPerHit.Value;
                    }
                    if (SimpleDdgiExactLocalLightThreshold.HasValue)
                        settings.SimpleDdgiExactLocalLightThreshold = SimpleDdgiExactLocalLightThreshold.Value;
                    if (SimpleDdgiLightTreeUniformMixtureProbability.HasValue)
                    {
                        settings.SimpleDdgiLightTreeUniformMixtureProbability =
                            SimpleDdgiLightTreeUniformMixtureProbability.Value;
                    }
                    if (SimpleDdgiLightTreeMaximumRefitAge.HasValue)
                        settings.SimpleDdgiLightTreeMaximumRefitAge = SimpleDdgiLightTreeMaximumRefitAge.Value;
                    if (DdgiDynamicBlasMemoryBudgetBytes.HasValue)
                        settings.DdgiDynamicBlasMemoryBudgetBytes = DdgiDynamicBlasMemoryBudgetBytes.Value;
                    if (DdgiDynamicBlasScratchBudgetBytes.HasValue)
                        settings.DdgiDynamicBlasScratchBudgetBytes = DdgiDynamicBlasScratchBudgetBytes.Value;
                    if (DdgiDynamicBlasBuildsPerFrame.HasValue)
                        settings.DdgiDynamicBlasBuildsPerFrame = DdgiDynamicBlasBuildsPerFrame.Value;
                    if (DdgiDynamicBlasPrimitivesPerFrame.HasValue)
                        settings.DdgiDynamicBlasPrimitivesPerFrame = DdgiDynamicBlasPrimitivesPerFrame.Value;
                    if (DdgiFoliageProxyTriangleBudget.HasValue)
                        settings.DdgiFoliageProxyTriangleBudget = DdgiFoliageProxyTriangleBudget.Value;
                    if (DdgiFoliageProxyUpdateCadenceFrames.HasValue)
                    {
                        settings.DdgiFoliageProxyUpdateCadenceFrames =
                            DdgiFoliageProxyUpdateCadenceFrames.Value;
                    }
                    if (DdgiTransparencyCandidateLimit.HasValue)
                        settings.DdgiTransparencyCandidateLimit = DdgiTransparencyCandidateLimit.Value;
                    if (DdgiTransparencyLayerLimit.HasValue)
                        settings.DdgiTransparencyLayerLimit = DdgiTransparencyLayerLimit.Value;
                    if (DdgiDecalCandidateLimit.HasValue)
                        settings.DdgiDecalCandidateLimit = DdgiDecalCandidateLimit.Value;
                    if (SimpleDdgiDirectionalRadianceMemoryBudgetBytes.HasValue)
                    {
                        settings.SimpleDdgiDirectionalRadianceMemoryBudgetBytes =
                            SimpleDdgiDirectionalRadianceMemoryBudgetBytes.Value;
                    }
                    if (SimpleDdgiRoughSpecularMinimumRoughness.HasValue)
                    {
                        settings.SimpleDdgiRoughSpecularMinimumRoughness =
                            SimpleDdgiRoughSpecularMinimumRoughness.Value;
                    }
                    if (SimpleDdgiRoughSpecularFullWeightRoughness.HasValue)
                    {
                        settings.SimpleDdgiRoughSpecularFullWeightRoughness =
                            SimpleDdgiRoughSpecularFullWeightRoughness.Value;
                    }
                }
                settings.SimpleDdgiProbeSpacing = SimpleDdgiProbeSpacing;
                settings.SimpleDdgiRingCount = SimpleDdgiRingCount;
                settings.SimpleDdgiRingBaseSpacing = SimpleDdgiRingBaseSpacing;
                settings.SimpleDdgiRingSpacingMultiplier = SimpleDdgiRingSpacingMultiplier;
                settings.SimpleDdgiViewForwardPlacementFraction = SimpleDdgiViewForwardPlacementFraction;
                settings.SimpleDdgiVerticalRingPolicy = SimpleDdgiVerticalRingPolicy;
                settings.SimpleDdgiReceiverVerticalAnchor = SimpleDdgiReceiverVerticalAnchor;
                settings.SimpleDdgiVerticalRecenterHysteresisFraction = SimpleDdgiVerticalRecenterHysteresisFraction;
                settings.SimpleDdgiNearRingGridSizeX = SimpleDdgiNearRingGridSizeX;
                settings.SimpleDdgiNearRingGridSizeY = SimpleDdgiNearRingGridSizeY;
                settings.SimpleDdgiNearRingGridSizeZ = SimpleDdgiNearRingGridSizeZ;
                settings.SimpleDdgiMidRingGridSizeX = SimpleDdgiMidRingGridSizeX;
                settings.SimpleDdgiMidRingGridSizeY = SimpleDdgiMidRingGridSizeY;
                settings.SimpleDdgiMidRingGridSizeZ = SimpleDdgiMidRingGridSizeZ;
                settings.SimpleDdgiFarRingGridSizeX = SimpleDdgiFarRingGridSizeX;
                settings.SimpleDdgiFarRingGridSizeY = SimpleDdgiFarRingGridSizeY;
                settings.SimpleDdgiFarRingGridSizeZ = SimpleDdgiFarRingGridSizeZ;
                if (SimpleDdgiRefinementBricksEnabled.HasValue)
                    settings.SimpleDdgiRefinementBricksEnabled = SimpleDdgiRefinementBricksEnabled.Value;
                if (SimpleDdgiRefinementMaximumBricks.HasValue)
                    settings.SimpleDdgiRefinementMaximumBricks = SimpleDdgiRefinementMaximumBricks.Value;
                if (SimpleDdgiRefinementGridSizeX.HasValue)
                    settings.SimpleDdgiRefinementGridSizeX = SimpleDdgiRefinementGridSizeX.Value;
                if (SimpleDdgiRefinementGridSizeY.HasValue)
                    settings.SimpleDdgiRefinementGridSizeY = SimpleDdgiRefinementGridSizeY.Value;
                if (SimpleDdgiRefinementGridSizeZ.HasValue)
                    settings.SimpleDdgiRefinementGridSizeZ = SimpleDdgiRefinementGridSizeZ.Value;
                if (SimpleDdgiRefinementSpacingScale.HasValue)
                    settings.SimpleDdgiRefinementSpacingScale = SimpleDdgiRefinementSpacingScale.Value;
                if (SimpleDdgiRefinementRetentionFrames.HasValue)
                    settings.SimpleDdgiRefinementRetentionFrames = SimpleDdgiRefinementRetentionFrames.Value;
                if (SimpleDdgiRefinementMinimumEmissiveLuminanceNits.HasValue)
                {
                    settings.SimpleDdgiRefinementMinimumEmissiveLuminanceNits =
                        SimpleDdgiRefinementMinimumEmissiveLuminanceNits.Value;
                }
                if (SimpleDdgiRefinementMaximumEmitterAreaSquareMeters.HasValue)
                {
                    settings.SimpleDdgiRefinementMaximumEmitterAreaSquareMeters =
                        SimpleDdgiRefinementMaximumEmitterAreaSquareMeters.Value;
                }
                if (SimpleDdgiNearVisibilitySidecarEnabled.HasValue)
                {
                    settings.SimpleDdgiNearVisibilitySidecarEnabled =
                        SimpleDdgiNearVisibilitySidecarEnabled.Value;
                }
                if (SimpleDdgiNearVisibilitySidecarMemoryBudgetBytes.HasValue)
                {
                    settings.SimpleDdgiNearVisibilitySidecarMemoryBudgetBytes =
                        SimpleDdgiNearVisibilitySidecarMemoryBudgetBytes.Value;
                }
                settings.SimpleDdgiRaysPerProbe = SimpleDdgiRaysPerProbe;
                settings.SimpleDdgiMaintenanceRaysPerProbe = SimpleDdgiMaintenanceRaysPerProbe;
                settings.SimpleDdgiNearFullRaysPerProbe = SimpleDdgiNearFullRaysPerProbe;
                settings.SimpleDdgiMidFullRaysPerProbe = SimpleDdgiMidFullRaysPerProbe;
                settings.SimpleDdgiFarFullRaysPerProbe = SimpleDdgiFarFullRaysPerProbe;
                settings.SimpleDdgiNearMaintenanceRaysPerProbe = SimpleDdgiNearMaintenanceRaysPerProbe;
                settings.SimpleDdgiMidMaintenanceRaysPerProbe = SimpleDdgiMidMaintenanceRaysPerProbe;
                settings.SimpleDdgiFarMaintenanceRaysPerProbe = SimpleDdgiFarMaintenanceRaysPerProbe;
                settings.SimpleDdgiNearMinimumUpdateQuota = SimpleDdgiNearMinimumUpdateQuota;
                settings.SimpleDdgiMidMinimumUpdateQuota = SimpleDdgiMidMinimumUpdateQuota;
                settings.SimpleDdgiFarMinimumUpdateQuota = SimpleDdgiFarMinimumUpdateQuota;
                settings.SimpleDdgiNearMaximumUpdateQuota = SimpleDdgiNearMaximumUpdateQuota;
                settings.SimpleDdgiMidMaximumUpdateQuota = SimpleDdgiMidMaximumUpdateQuota;
                settings.SimpleDdgiFarMaximumUpdateQuota = SimpleDdgiFarMaximumUpdateQuota;
                settings.SimpleDdgiNearMaterialTextureMaxCascade = SimpleDdgiNearMaterialTextureMaxCascade;
                settings.SimpleDdgiMidMaterialTextureMaxCascade = SimpleDdgiMidMaterialTextureMaxCascade;
                settings.SimpleDdgiFarMaterialTextureMaxCascade = SimpleDdgiFarMaterialTextureMaxCascade;
                if (sourceVersion < 9)
                {
                    settings.SimpleDdgiNearLocalLightSamplesPerHit = SimpleDdgiNearMaxShadedLights;
                    settings.SimpleDdgiMidLocalLightSamplesPerHit = SimpleDdgiMidMaxShadedLights;
                    settings.SimpleDdgiFarLocalLightSamplesPerHit = SimpleDdgiFarMaxShadedLights;
                }
                settings.SimpleDdgiHysteresis = SimpleDdgiHysteresis;
                settings.SimpleDdgiHysteresisChangeThreshold = SimpleDdgiHysteresisChangeThreshold;
                settings.SimpleDdgiHysteresisStepThreshold = SimpleDdgiHysteresisStepThreshold;
                settings.SimpleDdgiLightingDirtyFrameCount = SimpleDdgiLightingDirtyFrameCount;
                settings.SimpleDdgiStableMaintenanceUpdateCount = SimpleDdgiStableMaintenanceUpdateCount;
                settings.SimpleDdgiStableMaintenanceEmaThreshold = SimpleDdgiStableMaintenanceEmaThreshold;
                settings.SimpleDdgiTransportSolverRelaxation = SimpleDdgiTransportSolverRelaxation;
                settings.SimpleDdgiTransportAlbedoClamp = SimpleDdgiTransportAlbedoClamp;
                if (SimpleDdgiTransportTailRelativeTolerance.HasValue)
                {
                    settings.SimpleDdgiTransportTailRelativeTolerance =
                        SimpleDdgiTransportTailRelativeTolerance.Value;
                }
                else if (TryGetLegacyFloat("SimpleDdgiTransportResidualThreshold", out float legacyTailTolerance))
                {
                    settings.SimpleDdgiTransportTailRelativeTolerance = legacyTailTolerance;
                }

                if (SimpleDdgiTransportAcceleratedSweepCount.HasValue)
                    settings.SimpleDdgiTransportAcceleratedSweepCount = SimpleDdgiTransportAcceleratedSweepCount.Value;
                if (SimpleDdgiTransportAccelerationEnabled.HasValue)
                    settings.SimpleDdgiTransportAccelerationEnabled = SimpleDdgiTransportAccelerationEnabled.Value;
                if (SimpleDdgiTransportTailCertificationEnabled.HasValue)
                    settings.SimpleDdgiTransportTailCertificationEnabled = SimpleDdgiTransportTailCertificationEnabled.Value;

                // The old generation count is still loaded for tooling and
                // diagnostics, but it no longer controls V2 retirement.
                if (TryGetLegacyInt("SimpleDdgiTransportMaximumSolverGenerations", out int legacyMaximumGenerations))
                    settings.SimpleDdgiTransportMaximumSolverGenerations = legacyMaximumGenerations;
                settings.SimpleDdgiTransportSourceRefreshFrames = SimpleDdgiTransportSourceRefreshFrames;
                settings.SimpleDdgiAutomaticProbeDensityScale = SimpleDdgiAutomaticProbeDensityScale;
                settings.SimpleDdgiNormalBias = SimpleDdgiNormalBias;
                settings.SimpleDdgiViewBias = SimpleDdgiViewBias;
                settings.SimpleDdgiMaximumWorldBiasMeters = SimpleDdgiMaximumWorldBiasMeters;
                settings.SimpleDdgiArchitecturalThicknessMeters = SimpleDdgiArchitecturalThicknessMeters;
                settings.SimpleDdgiProbeUpdatesPerFrame = SimpleDdgiProbeUpdatesPerFrame;
                settings.FarFieldClipmapEnabled = FarFieldClipmapEnabled;
                settings.FarFieldPagedEnabled = FarFieldPagedEnabled;
                settings.GiFarFieldMaterialV2 = GiFarFieldMaterialV2;
                settings.FarFieldSkyVisibilityEnabled = FarFieldSkyVisibilityEnabled;
                settings.FarFieldSunShadowEnabled = FarFieldSunShadowEnabled;
                settings.FarFieldClipmapResolution = FarFieldClipmapResolution;
                settings.FarFieldStartDistance = FarFieldStartDistance;
                settings.FarFieldMaxTraceSteps = FarFieldMaxTraceSteps;
                settings.FarFieldPageResolution = FarFieldPageResolution;
                settings.FarFieldCascadeCount = FarFieldCascadeCount;
                settings.FarFieldResidentPageBudget = FarFieldResidentPageBudget;
                settings.FarFieldPageUpdatesPerFrame = FarFieldPageUpdatesPerFrame;
                settings.FarFieldPageRequestRadius = FarFieldPageRequestRadius;
                settings.FarFieldBaseVoxelSize = FarFieldBaseVoxelSize;
                settings.FarFieldCascadeVoxelScale = FarFieldCascadeVoxelScale;
                settings.FarFieldMemoryBudgetBytes = FarFieldMemoryBudgetBytes;
                settings.FarFieldForceAll = FarFieldForceAll;
                settings.StreamedGiAccelerationStructuresEnabled = StreamedGiAccelerationStructuresEnabled;
                settings.GiAccelerationStructureMemoryBudgetBytes = GiAccelerationStructureMemoryBudgetBytes;
                settings.GiAccelerationStructureStaticResidentDistance = GiAccelerationStructureStaticResidentDistance;
                settings.GiAccelerationStructureMaximumStaticInstances = GiAccelerationStructureMaximumStaticInstances;
                settings.GiAccelerationStructureEvictionGraceFrames = GiAccelerationStructureEvictionGraceFrames;
                settings.DdgiAsyncComputeEnabled = DdgiAsyncComputeEnabled;
                settings.DdgiProbeUpdatePrimaryRayBudget = DdgiProbeUpdatePrimaryRayBudget;
                settings.DdgiMaxShadedLights = DdgiMaxShadedLights;
                settings.DdgiMaterialTextureMaxCascade = DdgiMaterialTextureMaxCascade;
                settings.DdgiAtlasMemoryBudgetBytes = DdgiAtlasMemoryBudgetBytes;
                settings.DdgiThinWallLeakClampStrength = DdgiThinWallLeakClampStrength;
                settings.DdgiSelfShadowBiasScale = DdgiSelfShadowBiasScale;
                settings.ResolutionScale = ResolutionScale;
                settings.MaxBounceDistance = MaxBounceDistance;
                settings.TemporalEnabled = TemporalEnabled;
                settings.DenoiserEnabled = DenoiserEnabled;
                settings.HistoryResponsiveness = HistoryResponsiveness;
                settings.NormalRejectionThreshold = NormalRejectionThreshold;
                settings.DepthRejectionThreshold = DepthRejectionThreshold;
                settings.LeakClampStrength = LeakClampStrength;
            }

            private bool TryGetLegacyFloat(string name, out float value)
            {
                value = default;
                if (ExtensionData == null)
                    return false;

                foreach (KeyValuePair<string, JsonElement> entry in ExtensionData)
                {
                    if (!string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase) ||
                        !entry.Value.TryGetSingle(out float parsed) ||
                        !float.IsFinite(parsed))
                    {
                        continue;
                    }

                    value = parsed;
                    return true;
                }

                return false;
            }

            private bool TryGetLegacyInt(string name, out int value)
            {
                value = default;
                if (ExtensionData == null)
                    return false;

                foreach (KeyValuePair<string, JsonElement> entry in ExtensionData)
                {
                    if (!string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase) ||
                        !entry.Value.TryGetInt32(out int parsed))
                    {
                        continue;
                    }

                    value = parsed;
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Persisted authored-volume contract. Keep vectors as named scalar
        /// properties: <see cref="Vector3"/> intentionally exposes fields for
        /// interop, while the settings JSON contract must remain field-agnostic
        /// and stable across serializer option changes.
        /// </summary>
        private sealed record SimpleDdgiAuthoredVolumeFile
        {
            public Vector3File? Min { get; init; } = new();
            public Vector3File? Max { get; init; } = new();
            public float Spacing { get; init; } = 1.0f;
            public Vector3File? LatticePhase { get; init; } = new();
            public SimpleDdgiVolumePurpose Purpose { get; init; } = SimpleDdgiVolumePurpose.ReceiverHero;
            public int Priority { get; init; }

            public static SimpleDdgiAuthoredVolumeFile FromSettings(SimpleDdgiAuthoredVolume volume) => new()
            {
                Min = Vector3File.FromVector3(volume.Min),
                Max = Vector3File.FromVector3(volume.Max),
                Spacing = volume.Spacing,
                LatticePhase = Vector3File.FromVector3(volume.LatticePhase),
                Purpose = volume.Purpose,
                Priority = volume.Priority
            };

            public SimpleDdgiAuthoredVolume ToSettings() => new(
                Min?.ToVector3() ?? Vector3.Zero,
                Max?.ToVector3() ?? Vector3.Zero,
                Spacing,
                LatticePhase?.ToVector3() ?? Vector3.Zero,
                Purpose,
                Priority);
        }

        /// <summary>Named scalar serialization for the engine math vector ABI.</summary>
        private sealed record Vector3File
        {
            public float X { get; init; }
            public float Y { get; init; }
            public float Z { get; init; }

            public static Vector3File FromVector3(Vector3 value) => new()
            {
                X = value.X,
                Y = value.Y,
                Z = value.Z
            };

            public Vector3 ToVector3() => new(X, Y, Z);
        }

        private sealed record AsyncComputeFile
        {
            public AsyncComputeMode? Mode { get; init; }

            // Version 1 persisted a single bool. Keep accepting it, but do not emit it from
            // new files so a modern mode cannot be overwritten by JSON property ordering.
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public bool? Enabled { get; init; }
            public bool HiZBuildEnabled { get; init; } = true;
            public bool AmbientOcclusionBlurEnabled { get; init; } = true;
            public bool FogEnabled { get; init; } = true;
            public bool BloomEnabled { get; init; } = true;
            public bool SimpleDdgiUpdateEnabled { get; init; } = true;
            public bool FarFieldClipmapBakeEnabled { get; init; } = true;
            public bool GpuParticlesEnabled { get; init; } = true;
            public int AutoMinimumSampleCount { get; init; } = 30;
            public int AutoWarmupFrameCount { get; init; } = 60;
            public float AutoMinimumAbsoluteBenefitMilliseconds { get; init; } = 0.25f;
            public float AutoMinimumRelativeBenefit { get; init; } = 0.03f;
            public int AutoDecisionCooldownFrames { get; init; } = 180;

            public static AsyncComputeFile FromSettings(AsyncComputeSettings settings)
            {
                return new AsyncComputeFile
                {
                    Mode = settings.Mode,
                    HiZBuildEnabled = settings.HiZBuildEnabled,
                    AmbientOcclusionBlurEnabled = settings.AmbientOcclusionBlurEnabled,
                    FogEnabled = settings.FogEnabled,
                    BloomEnabled = settings.BloomEnabled,
                    SimpleDdgiUpdateEnabled = settings.SimpleDdgiUpdateEnabled,
                    FarFieldClipmapBakeEnabled = settings.FarFieldClipmapBakeEnabled,
                    GpuParticlesEnabled = settings.GpuParticlesEnabled,
                    AutoMinimumSampleCount = settings.AutoMinimumSampleCount,
                    AutoWarmupFrameCount = settings.AutoWarmupFrameCount,
                    AutoMinimumAbsoluteBenefitMilliseconds = settings.AutoMinimumAbsoluteBenefitMilliseconds,
                    AutoMinimumRelativeBenefit = settings.AutoMinimumRelativeBenefit,
                    AutoDecisionCooldownFrames = settings.AutoDecisionCooldownFrames
                };
            }

            public void ApplyTo(AsyncComputeSettings settings, bool missingModeMeansDisabled)
            {
                settings.Mode = Mode.HasValue && Enum.IsDefined(Mode.Value)
                    ? Mode.Value
                    : Enabled.HasValue
                        ? Enabled.Value
                            ? AsyncComputeMode.ForceEnabledForValidation
                            : AsyncComputeMode.Disabled
                        : missingModeMeansDisabled
                            ? AsyncComputeMode.Disabled
                            : AsyncComputeMode.Auto;
                settings.HiZBuildEnabled = HiZBuildEnabled;
                settings.AmbientOcclusionBlurEnabled = AmbientOcclusionBlurEnabled;
                settings.FogEnabled = FogEnabled;
                settings.BloomEnabled = BloomEnabled;
                settings.SimpleDdgiUpdateEnabled = SimpleDdgiUpdateEnabled;
                settings.FarFieldClipmapBakeEnabled = FarFieldClipmapBakeEnabled;
                settings.GpuParticlesEnabled = GpuParticlesEnabled;
                settings.AutoMinimumSampleCount = Math.Clamp(AutoMinimumSampleCount, 1, 4_096);
                settings.AutoWarmupFrameCount = Math.Clamp(AutoWarmupFrameCount, 0, 16_384);
                settings.AutoMinimumAbsoluteBenefitMilliseconds = ClampFinite(AutoMinimumAbsoluteBenefitMilliseconds, 0.0f, 100.0f, 0.25f);
                settings.AutoMinimumRelativeBenefit = ClampFinite(AutoMinimumRelativeBenefit, 0.0f, 1.0f, 0.03f);
                settings.AutoDecisionCooldownFrames = Math.Clamp(AutoDecisionCooldownFrames, 0, 16_384);
            }

            private static float ClampFinite(float value, float minimum, float maximum, float fallback) =>
                float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
        }

        private sealed record SceneSubmissionFile
        {
            public bool GpuCompactionEnabled { get; init; } = true;
            public bool IndirectMeshletDispatchEnabled { get; init; } = true;
            public bool GpuLodSelectionEnabled { get; init; } = true;
            public GpuLodSelectionMode? GpuLodSelectionMode { get; init; }
            public float? GpuLodTargetPixelError { get; init; }
            public float GpuLod1DistanceRatio { get; init; } = SceneSubmissionSettings.DefaultGpuLod1DistanceRatio;
            public float GpuLod2DistanceRatio { get; init; } = SceneSubmissionSettings.DefaultGpuLod2DistanceRatio;
            public bool GpuShadowCompactionEnabled { get; init; } = true;
            public int GpuShadowLodBias { get; init; } = SceneSubmissionSettings.DefaultGpuShadowLodBias;
            public bool ValidationCompareCpuGpuLists { get; init; }

            public static SceneSubmissionFile FromSettings(SceneSubmissionSettings settings)
            {
                return new SceneSubmissionFile
                {
                    GpuCompactionEnabled = settings.GpuCompactionEnabled,
                    IndirectMeshletDispatchEnabled = settings.IndirectMeshletDispatchEnabled,
                    GpuLodSelectionEnabled = settings.GpuLodSelectionEnabled,
                    GpuLodSelectionMode = settings.GpuLodSelectionMode,
                    GpuLodTargetPixelError =
                        settings.GpuLodTargetPixelError,
                    GpuLod1DistanceRatio = settings.GpuLod1DistanceRatio,
                    GpuLod2DistanceRatio = settings.GpuLod2DistanceRatio,
                    GpuShadowCompactionEnabled = settings.GpuShadowCompactionEnabled,
                    GpuShadowLodBias = settings.GpuShadowLodBias,
                    ValidationCompareCpuGpuLists = settings.ValidationCompareCpuGpuLists
                };
            }

            public void ApplyTo(
                SceneSubmissionSettings settings,
                int serializationVersion)
            {
                settings.GpuCompactionEnabled = GpuCompactionEnabled;
                settings.IndirectMeshletDispatchEnabled = IndirectMeshletDispatchEnabled;
                settings.GpuLodSelectionEnabled = GpuLodSelectionEnabled;
                settings.GpuLodSelectionMode = serializationVersion >= 23
                    ? GpuLodSelectionMode ??
                        global::Njulf.Rendering.Data.GpuLodSelectionMode.ScreenSpaceError
                    : global::Njulf.Rendering.Data.GpuLodSelectionMode.LegacyDistance;
                settings.GpuLodTargetPixelError = serializationVersion >= 23
                    ? GpuLodTargetPixelError ??
                        SceneSubmissionSettings.DefaultGpuLodTargetPixelError
                    : SceneSubmissionSettings.DefaultGpuLodTargetPixelError;
                settings.GpuLod1DistanceRatio = GpuLod1DistanceRatio;
                settings.GpuLod2DistanceRatio = GpuLod2DistanceRatio;
                settings.GpuShadowCompactionEnabled = GpuShadowCompactionEnabled;
                settings.GpuShadowLodBias = GpuShadowLodBias;
                settings.ValidationCompareCpuGpuLists = ValidationCompareCpuGpuLists;
            }
        }

        private sealed record HiZOcclusionFile
        {
            public bool Enabled { get; init; } = true;
            public bool AdaptiveEnabled { get; init; } = true;
            public bool PreviousFrameSceneSubmissionEnabled { get; init; } = true;
            public bool CurrentFrameForwardVisibilityEnabled { get; init; } = true;
            public bool ForceOn { get; init; }
            public bool ForceProbe { get; init; }
            public bool ValidateAgainstLegacyPath { get; init; }
            public bool SceneSubmissionPreviousFrameCullingEnabled { get; init; } = true;
            public bool CurrentFrameForwardVisibilityCompactionEnabled { get; init; } = true;
            public int PreviousFrameUvPaddingPixels { get; init; } = 8;
            public float OcclusionBias { get; init; } = 0.0005f;
            public bool DisablePreviousFrameCullingDuringFastCameraMotion { get; init; } = true;
            public float FastCameraMotionDistanceThreshold { get; init; } = 1.0f;
            public float FastCameraMotionForwardDotThreshold { get; init; } = 0.985f;
            public int CameraMotionSuppressionFrames { get; init; } = 1;

            public static HiZOcclusionFile FromSettings(HiZOcclusionSettings settings)
            {
                return new HiZOcclusionFile
                {
                    Enabled = settings.Enabled,
                    AdaptiveEnabled = settings.AdaptiveEnabled,
                    PreviousFrameSceneSubmissionEnabled = settings.PreviousFrameSceneSubmissionEnabled,
                    CurrentFrameForwardVisibilityEnabled = settings.CurrentFrameForwardVisibilityEnabled,
                    ForceOn = settings.ForceOn,
                    ForceProbe = settings.ForceProbe,
                    ValidateAgainstLegacyPath = settings.ValidateAgainstLegacyPath,
                    SceneSubmissionPreviousFrameCullingEnabled = settings.SceneSubmissionPreviousFrameCullingEnabled,
                    CurrentFrameForwardVisibilityCompactionEnabled = settings.CurrentFrameForwardVisibilityCompactionEnabled,
                    PreviousFrameUvPaddingPixels = settings.PreviousFrameUvPaddingPixels,
                    OcclusionBias = settings.OcclusionBias,
                    DisablePreviousFrameCullingDuringFastCameraMotion = settings.DisablePreviousFrameCullingDuringFastCameraMotion,
                    FastCameraMotionDistanceThreshold = settings.FastCameraMotionDistanceThreshold,
                    FastCameraMotionForwardDotThreshold = settings.FastCameraMotionForwardDotThreshold,
                    CameraMotionSuppressionFrames = settings.CameraMotionSuppressionFrames
                };
            }

            public void ApplyTo(HiZOcclusionSettings settings)
            {
                settings.Enabled = Enabled;
                settings.AdaptiveEnabled = AdaptiveEnabled;
                settings.SceneSubmissionPreviousFrameCullingEnabled =
                    PreviousFrameSceneSubmissionEnabled || SceneSubmissionPreviousFrameCullingEnabled;
                settings.CurrentFrameForwardVisibilityCompactionEnabled =
                    CurrentFrameForwardVisibilityEnabled || CurrentFrameForwardVisibilityCompactionEnabled;
                settings.ForceOn = ForceOn;
                settings.ForceProbe = ForceProbe;
                settings.ValidateAgainstLegacyPath = ValidateAgainstLegacyPath;
                settings.PreviousFrameUvPaddingPixels = PreviousFrameUvPaddingPixels;
                settings.OcclusionBias = OcclusionBias;
                settings.DisablePreviousFrameCullingDuringFastCameraMotion = DisablePreviousFrameCullingDuringFastCameraMotion;
                settings.FastCameraMotionDistanceThreshold = FastCameraMotionDistanceThreshold;
                settings.FastCameraMotionForwardDotThreshold = FastCameraMotionForwardDotThreshold;
                settings.CameraMotionSuppressionFrames = CameraMotionSuppressionFrames;
            }
        }

        private sealed record FoliageFile
        {
            public bool Enabled { get; init; } = true;
            public bool GpuDrivenEnabled { get; init; } = true;
            public bool HiZCullingEnabled { get; init; } = true;
            public bool CastShadows { get; init; } = true;
            public bool IndirectMeshletDispatchEnabled { get; init; } = true;
            public bool FarImpostorsEnabled { get; init; } = true;
            public bool MotionVectorsEnabled { get; init; } = true;
            public bool LocalShadowsEnabled { get; init; } = true;
            public float GrassShadowDistance { get; init; } = 25f;
            public float GrassShadowDensityScale { get; init; } = 0.5f;
            public float MaxDrawDistance { get; init; } = 250f;
            public float DensityScale { get; init; } = 1f;
            public int MaxVisibleClusters { get; init; } = 262144;
            public int MaxVisibleMeshletDraws { get; init; } = 524288;
            public int MaxLocalShadowedSpotLights { get; init; } = 1;
            public int MaxLocalShadowedPointLights { get; init; } = 1;
            public int MaxLocalShadowClusters { get; init; } = 4096;
            public int MaxLocalShadowMeshletDraws { get; init; } = 8192;
            public FoliageDebugView DebugView { get; init; } = FoliageDebugView.None;

            public static FoliageFile FromSettings(FoliageSettings settings)
            {
                return new FoliageFile
                {
                    Enabled = settings.Enabled,
                    GpuDrivenEnabled = settings.GpuDrivenEnabled,
                    HiZCullingEnabled = settings.HiZCullingEnabled,
                    CastShadows = settings.CastShadows,
                    IndirectMeshletDispatchEnabled = settings.IndirectMeshletDispatchEnabled,
                    FarImpostorsEnabled = settings.FarImpostorsEnabled,
                    MotionVectorsEnabled = settings.MotionVectorsEnabled,
                    LocalShadowsEnabled = settings.LocalShadowsEnabled,
                    GrassShadowDistance = settings.GrassShadowDistance,
                    GrassShadowDensityScale = settings.GrassShadowDensityScale,
                    MaxDrawDistance = settings.MaxDrawDistance,
                    DensityScale = settings.DensityScale,
                    MaxVisibleClusters = settings.MaxVisibleClusters,
                    MaxVisibleMeshletDraws = settings.MaxVisibleMeshletDraws,
                    MaxLocalShadowedSpotLights = settings.MaxLocalShadowedSpotLights,
                    MaxLocalShadowedPointLights = settings.MaxLocalShadowedPointLights,
                    MaxLocalShadowClusters = settings.MaxLocalShadowClusters,
                    MaxLocalShadowMeshletDraws = settings.MaxLocalShadowMeshletDraws,
                    DebugView = settings.DebugView
                };
            }

            public void ApplyTo(FoliageSettings settings)
            {
                settings.Enabled = Enabled;
                settings.GpuDrivenEnabled = GpuDrivenEnabled;
                settings.HiZCullingEnabled = HiZCullingEnabled;
                settings.CastShadows = CastShadows;
                settings.IndirectMeshletDispatchEnabled = IndirectMeshletDispatchEnabled;
                settings.FarImpostorsEnabled = FarImpostorsEnabled;
                settings.MotionVectorsEnabled = MotionVectorsEnabled;
                settings.LocalShadowsEnabled = LocalShadowsEnabled;
                settings.GrassShadowDistance = GrassShadowDistance;
                settings.GrassShadowDensityScale = GrassShadowDensityScale;
                settings.MaxDrawDistance = MaxDrawDistance;
                settings.DensityScale = DensityScale;
                settings.MaxVisibleClusters = MaxVisibleClusters;
                settings.MaxVisibleMeshletDraws = MaxVisibleMeshletDraws;
                settings.MaxLocalShadowedSpotLights = MaxLocalShadowedSpotLights;
                settings.MaxLocalShadowedPointLights = MaxLocalShadowedPointLights;
                settings.MaxLocalShadowClusters = MaxLocalShadowClusters;
                settings.MaxLocalShadowMeshletDraws = MaxLocalShadowMeshletDraws;
                settings.DebugView = DebugView;
            }
        }

        private sealed record DynamicResolutionFile
        {
            public bool Enabled { get; init; } = true;
            public float MinimumScale { get; init; } = 0.7f;
            public float MaximumScale { get; init; } = 1.0f;
            public float TargetFrameMilliseconds { get; init; } = 16.67f;
            public float AdjustmentRate { get; init; } = 0.05f;

            public static DynamicResolutionFile FromSettings(DynamicResolutionSettings settings)
            {
                return new DynamicResolutionFile
                {
                    Enabled = settings.Enabled,
                    MinimumScale = settings.MinimumScale,
                    MaximumScale = settings.MaximumScale,
                    TargetFrameMilliseconds = settings.TargetFrameMilliseconds,
                    AdjustmentRate = settings.AdjustmentRate
                };
            }

            public void ApplyTo(DynamicResolutionSettings settings)
            {
                settings.Enabled = Enabled;
                settings.MinimumScale = MinimumScale;
                settings.MaximumScale = MaximumScale;
                settings.TargetFrameMilliseconds = TargetFrameMilliseconds;
                settings.AdjustmentRate = AdjustmentRate;
            }
        }
    }
}
