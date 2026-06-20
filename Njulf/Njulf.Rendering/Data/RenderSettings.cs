using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Core.Math;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Diagnostics;

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
        LocalShadowSelection = 7
    }

    public sealed class ShadowSettings
    {
        public const int MaxDirectionalCascades = 4;

        private uint _directionalShadowMapSize = 2048;
        private int _directionalCascadeCount = 2;
        private float _maxShadowDistance = 80f;
        private float _normalBias = 0.03f;
        private float _slopeScaledDepthBias = 1.5f;
        private float _constantDepthBias = 0.0005f;
        private int _pcfRadius = 1;
        private int _maxShadowedSpotLights;
        private uint _spotShadowAtlasSize = 4096;
        private uint _spotShadowTileSize = 512;
        private float _spotNormalBias = 0.02f;
        private float _spotConstantDepthBias = 0.0005f;
        private float _spotSlopeScaledDepthBias = 1.5f;
        private int _spotPcfRadius = 1;
        private int _maxShadowedPointLights;
        private uint _pointShadowMapSize = 512;
        private float _pointNormalBias = 0.03f;
        private float _pointConstantDepthBias = 0.001f;
        private float _pointSlopeScaledDepthBias = 1.5f;
        private int _pointPcfRadius = 1;

        public bool DirectionalShadowsEnabled { get; set; } = true;
        public bool SpotShadowsEnabled { get; set; }
        public bool PointShadowsEnabled { get; set; }

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

        public float MaxShadowDistance
        {
            get => _maxShadowDistance;
            set => _maxShadowDistance = Clamp(value, 1f, 1000f);
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
        private float _targetLuminance = 0.18f;
        private float _minExposure = 0.05f;
        private float _maxExposure = 16.0f;
        private float _adaptationSpeed = 3.0f;
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

        public float AdaptationSpeed
        {
            get => _adaptationSpeed;
            set => _adaptationSpeed = Clamp(value, 0.0f, 30.0f);
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
        LinearDepth = 5
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
        FoggedScene = 8
    }

    public enum EnvironmentSourceKind : uint
    {
        ProceduralSky = 0,
        HdrEquirectangular = 1,
        Cubemap = 2
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
        StaticProbesAndPlanar = 4
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
        GlobalFallbackOnly = 10
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
        Dispersion = 54
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
        Ultra = 3
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
        public MaterialDebugView DebugView { get; set; } = MaterialDebugView.None;
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
        private int _maxLocalShadowedPointLights;
        private int _maxLocalShadowClusters = 4096;
        private int _maxLocalShadowMeshletDraws = 8192;

        public bool Enabled { get; set; } = true;
        public bool GpuDrivenEnabled { get; set; } = true;
        public bool HiZCullingEnabled { get; set; } = true;
        public bool CastShadows { get; set; } = true;
        public bool IndirectMeshletDispatchEnabled { get; set; } = true;
        public bool FarImpostorsEnabled { get; set; } = true;
        public bool MotionVectorsEnabled { get; set; }
        public bool LocalShadowsEnabled { get; set; }

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
        public bool GpuCompactionEnabled { get; set; }
        public bool IndirectMeshletDispatchEnabled { get; set; }
        public bool GpuLodSelectionEnabled { get; set; }
        public bool GpuShadowCompactionEnabled { get; set; }
        public bool ValidationCompareCpuGpuLists { get; set; }
    }

    public sealed class DynamicResolutionSettings
    {
        private float _minimumScale = 0.7f;
        private float _maximumScale = 1.0f;
        private float _targetFrameMilliseconds = 16.67f;
        private float _adjustmentRate = 0.05f;

        public bool Enabled { get; set; }

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

        public bool Enabled { get; set; } = true;
        public ParticleSimulationMode SimulationMode { get; set; } = ParticleSimulationMode.Cpu;
        public ParticleDebugView DebugView { get; set; } = ParticleDebugView.None;

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
        private int _maxTransparentMeshlets = 262144;
        private float _alphaDiscardThreshold = 0.001f;

        public bool Enabled { get; set; } = true;
        public TransparencyMode Mode { get; set; } = TransparencyMode.SortedAlphaBlend;
        public TransparencyDebugView DebugView { get; set; } = TransparencyDebugView.None;
        public bool ReceiveShadows { get; set; } = true;
        public bool SampleReflections { get; set; } = true;
        public bool SortPerMeshlet { get; set; } = true;

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
        private uint _environmentSize = 1024;
        private uint _irradianceSize = 64;
        private uint _prefilteredSize = 256;
        private uint _brdfLutSize = 256;
        private int _debugMipLevel;

        public bool Enabled { get; set; } = true;
        public EnvironmentSourceKind SourceKind { get; set; } = EnvironmentSourceKind.ProceduralSky;
        public string? SourcePath { get; set; }
        public EnvironmentTexturePrecision TexturePrecision { get; set; } = EnvironmentTexturePrecision.Float16;

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

        public float RotationRadians { get; set; }

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
            if (value < min)
                return min;
            return value > max ? max : value;
        }
    }

    public sealed class ReflectionSettings
    {
        public const int ShaderMaxProbesPerPixel = 4;

        private int _maxProbes = 8;
        private int _maxProbesPerPixel = 2;
        private uint _probeResolution = 128;
        private float _intensity = 1.0f;
        private float _globalFallbackIntensity = 1.0f;
        private int _maxProbeCapturesPerFrame;
        private int _debugProbeIndex;
        private int _debugCubemapFace;
        private int _debugMipLevel;

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
        private float _resolutionScale = 0.5f;
        private float _radius = 0.75f;
        private float _intensity = 1.0f;
        private float _bias = 0.03f;
        private float _power = 1.2f;
        private int _sampleCount = 16;
        private int _blurRadius = 2;
        private float _depthSigma = 2.0f;
        private float _normalSigma = 32.0f;

        public bool Enabled { get; set; } = true;
        public AmbientOcclusionMode Mode { get; set; } = AmbientOcclusionMode.Ssao;

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

    public sealed class AntiAliasingSettings
    {
        private float _fxaaContrastThreshold = 0.125f;
        private float _fxaaRelativeThreshold = 0.166f;
        private float _fxaaSubpixelBlending = 0.75f;
        private int _jitterSampleCount = 8;
        private float _taaFeedbackMin = 0.32f;
        private float _taaFeedbackMax = 0.64f;
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
                _taaFeedbackMin = Clamp(value, 0.2f, 0.98f);
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
            set => _taaVelocityRejectionScale = value < 0.0f ? 0.0f : value;
        }

        public AntiAliasingMode EffectiveMode => Mode;
        public int EffectiveSmaaSpatialSampleCount => GetSmaaPreset(EffectiveMode).SpatialSampleCount;
        public bool EffectiveSmaaUsesSpatialMultisampling => GetSmaaPreset(EffectiveMode).SpatialSampleCount > 1;
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

        private static SmaaPreset GetSmaaPreset(AntiAliasingMode mode)
        {
            return mode switch
            {
                AntiAliasingMode.SmaaLow => new SmaaPreset(0, 1, 0.10f, 16, 8, 25.0f),
                AntiAliasingMode.SmaaHigh => new SmaaPreset(2, 4, 0.10f, 16, 8, 25.0f),
                AntiAliasingMode.SmaaMedium => new SmaaPreset(1, 2, 0.10f, 16, 8, 25.0f),
                _ => new SmaaPreset(0, 0, 0.0f, 0, 0, 0.0f)
            };
        }

        private readonly struct SmaaPreset
        {
            public SmaaPreset(
                int quality,
                int spatialSampleCount,
                float threshold,
                int maxSearchSteps,
                int maxSearchStepsDiagonal,
                float cornerRounding)
            {
                Quality = quality;
                SpatialSampleCount = spatialSampleCount;
                Threshold = threshold;
                MaxSearchSteps = maxSearchSteps;
                MaxSearchStepsDiagonal = maxSearchStepsDiagonal;
                CornerRounding = cornerRounding;
            }

            public int Quality { get; }
            public int SpatialSampleCount { get; }
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

        private static float Clamp(float value, float min, float max)
        {
            if (value < min)
                return min;
            return value > max ? max : value;
        }
    }

    public sealed class RenderDiagnosticsSettings
    {
        public bool GpuMeshletCountersEnabled { get; set; }
    }

    public sealed class AsyncComputeSettings
    {
        public bool Enabled { get; set; }
        public bool HiZBuildEnabled { get; set; } = true;
        public bool AmbientOcclusionBlurEnabled { get; set; } = true;
        public bool FogEnabled { get; set; } = true;
        public bool BloomEnabled { get; set; } = true;
        public bool GpuParticlesEnabled { get; set; } = true;
    }

    public sealed class RenderSettings
    {
        private float _exposure = 1.0f;
        private float _resolutionScale = 1.0f;

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
        public AntiAliasingSettings AntiAliasing { get; } = new();
        public FogSettings Fog { get; } = new();
        public TransparencySettings Transparency { get; } = new();
        public DecalSettings Decals { get; } = new();
        public AnimationSettings Animation { get; } = new();
        public ParticleSettings Particles { get; } = new();
        public FoliageSettings Foliage { get; } = new();
        public SceneSubmissionSettings SceneSubmission { get; } = new();
        public MaterialSettings Materials { get; } = new();
        public RenderDiagnosticsSettings Diagnostics { get; } = new();
        public HiZVisibilityPolicySettings HiZVisibilityPolicy { get; } = new();
        public AsyncComputeSettings AsyncCompute { get; } = new();
        public DebugOverlaySettings Debug { get; } = new();
        public RenderBudgetSettings PerformanceBudgets { get; } = new();
        public RenderQualityPreset QualityPreset { get; private set; } = RenderQualityPreset.High;
        public RenderFeatureIsolationMode FeatureIsolation { get; set; } = RenderFeatureIsolationMode.FullFrame;
        public HiZTestMode HiZTestMode { get; set; } = HiZTestMode.Bounds4Tap;
        public bool UseSecondaryCommandBuffers { get; set; } = true;
        public bool UseCameraDependentCpuScenePayload { get; set; }
        public bool UseCpuMeshletFrustumCulling { get; set; }

        public void ApplyQualityPreset(RenderQualityPreset preset)
        {
            QualityPreset = preset;
            ShowRawHdrSceneColor = false;

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
                    Reflections.Enabled = false;
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
                    Shadows.SpotShadowsEnabled = false;
                    Shadows.MaxShadowedSpotLights = 0;
                    Shadows.PointShadowsEnabled = false;
                    Shadows.MaxShadowedPointLights = 0;
                    Transparency.Mode = TransparencyMode.SortedAlphaBlend;
                    break;
                case RenderQualityPreset.Medium:
                    ResolutionScale = 0.9f;
                    DynamicResolution.Enabled = true;
                    DynamicResolution.MinimumScale = 0.65f;
                    DynamicResolution.MaximumScale = 1.0f;
                    Bloom.Enabled = true;
                    Bloom.MipCount = 5;
                    Fog.Enabled = true;
                    AmbientOcclusion.Enabled = true;
                    AmbientOcclusion.ResolutionScale = 0.5f;
                    AmbientOcclusion.SampleCount = 8;
                    Reflections.Enabled = true;
                    Reflections.MaxProbesPerPixel = 1;
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
                    Shadows.MaxShadowedSpotLights = Math.Min(Shadows.MaxShadowedSpotLights, 2);
                    Shadows.MaxShadowedPointLights = Math.Min(Shadows.MaxShadowedPointLights, 1);
                    Transparency.Mode = TransparencyMode.SortedAlphaBlend;
                    break;
                case RenderQualityPreset.Ultra:
                    ResolutionScale = 1.0f;
                    DynamicResolution.Enabled = false;
                    Bloom.Enabled = true;
                    Bloom.MipCount = 8;
                    Fog.Enabled = true;
                    AmbientOcclusion.Enabled = true;
                    AmbientOcclusion.ResolutionScale = 1.0f;
                    AmbientOcclusion.SampleCount = 32;
                    Reflections.Enabled = true;
                    Reflections.MaxProbesPerPixel = ReflectionSettings.ShaderMaxProbesPerPixel;
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
                    Shadows.MaxShadowedSpotLights = Math.Max(Shadows.MaxShadowedSpotLights, 4);
                    Shadows.MaxShadowedPointLights = Math.Max(Shadows.MaxShadowedPointLights, 1);
                    Transparency.Mode = TransparencyMode.SortedAlphaBlend;
                    break;
                default:
                    ResolutionScale = 1.0f;
                    DynamicResolution.Enabled = false;
                    Bloom.Enabled = true;
                    Bloom.MipCount = 6;
                    Fog.Enabled = true;
                    AmbientOcclusion.Enabled = true;
                    AmbientOcclusion.ResolutionScale = 0.5f;
                    AmbientOcclusion.SampleCount = 16;
                    Reflections.Enabled = true;
                    Reflections.MaxProbesPerPixel = 2;
                    Particles.Enabled = true;
                    Foliage.Enabled = true;
                    Foliage.GpuDrivenEnabled = true;
                    Foliage.HiZCullingEnabled = true;
                    Foliage.CastShadows = true;
                    Foliage.DensityScale = 1.0f;
                    Foliage.MaxDrawDistance = 250f;
                    Foliage.GrassShadowDistance = 25f;
                    Foliage.GrassShadowDensityScale = 0.5f;
                    Foliage.LocalShadowsEnabled = false;
                    Foliage.MaxLocalShadowedSpotLights = 1;
                    Foliage.MaxLocalShadowedPointLights = 0;
                    Foliage.MaxLocalShadowClusters = 4096;
                    Foliage.MaxLocalShadowMeshletDraws = 8192;
                    Foliage.MaxVisibleClusters = 262144;
                    Foliage.MaxVisibleMeshletDraws = 524288;
                    AntiAliasing.Mode = AntiAliasingMode.SmaaMedium;
                    Shadows.DirectionalCascadeCount = 2;
                    Transparency.Mode = TransparencyMode.SortedAlphaBlend;
                    break;
            }
        }

        public void Save(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Render settings path cannot be null or empty.", nameof(path));

            string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var options = CreateJsonOptions();
            File.WriteAllText(path, JsonSerializer.Serialize(RenderSettingsFile.FromSettings(this), options));
        }

        public static RenderSettings Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Render settings path cannot be null or empty.", nameof(path));
            if (!File.Exists(path))
                throw new FileNotFoundException("Render settings file was not found.", path);

            RenderSettingsFile? file = JsonSerializer.Deserialize<RenderSettingsFile>(
                File.ReadAllText(path),
                CreateJsonOptions());
            if (file == null)
                throw new InvalidDataException($"Render settings file '{path}' did not contain a valid settings object.");

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
            public int Version { get; init; } = 1;
            public RenderQualityPreset QualityPreset { get; init; } = RenderQualityPreset.High;
            public float ResolutionScale { get; init; } = 1.0f;
            public DynamicResolutionFile DynamicResolution { get; init; } = new();
            public ToneMapper ToneMapper { get; init; } = ToneMapper.AcesFitted;
            public float Exposure { get; init; } = 1.0f;
            public bool AutoExposureEnabled { get; init; }
            public AntiAliasingMode AntiAliasingMode { get; init; } = AntiAliasingMode.SmaaMedium;
            public bool BloomEnabled { get; init; } = true;
            public bool AmbientOcclusionEnabled { get; init; } = true;
            public bool FogEnabled { get; init; } = true;
            public bool ReflectionsEnabled { get; init; } = true;
            public bool ShadowsEnabled { get; init; } = true;
            public bool ParticlesEnabled { get; init; } = true;
            public FoliageFile Foliage { get; init; } = new();
            public SceneSubmissionFile SceneSubmission { get; init; } = new();
            public AsyncComputeFile AsyncCompute { get; init; } = new();
            public bool GpuMeshletCountersEnabled { get; init; }

            public static RenderSettingsFile FromSettings(RenderSettings settings)
            {
                return new RenderSettingsFile
                {
                    QualityPreset = settings.QualityPreset,
                    ResolutionScale = settings.ResolutionScale,
                    DynamicResolution = DynamicResolutionFile.FromSettings(settings.DynamicResolution),
                    ToneMapper = settings.ToneMapper,
                    Exposure = settings.Exposure,
                    AutoExposureEnabled = settings.AutoExposure.Enabled,
                    AntiAliasingMode = settings.AntiAliasing.Mode,
                    BloomEnabled = settings.Bloom.Enabled,
                    AmbientOcclusionEnabled = settings.AmbientOcclusion.Enabled,
                    FogEnabled = settings.Fog.Enabled,
                    ReflectionsEnabled = settings.Reflections.Enabled,
                    ShadowsEnabled = settings.Shadows.DirectionalShadowsEnabled,
                    ParticlesEnabled = settings.Particles.Enabled,
                    Foliage = FoliageFile.FromSettings(settings.Foliage),
                    SceneSubmission = SceneSubmissionFile.FromSettings(settings.SceneSubmission),
                    AsyncCompute = AsyncComputeFile.FromSettings(settings.AsyncCompute),
                    GpuMeshletCountersEnabled = settings.Diagnostics.GpuMeshletCountersEnabled
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
                settings.AmbientOcclusion.Enabled = AmbientOcclusionEnabled;
                settings.Fog.Enabled = FogEnabled;
                settings.Reflections.Enabled = ReflectionsEnabled;
                settings.Shadows.DirectionalShadowsEnabled = ShadowsEnabled;
                settings.Particles.Enabled = ParticlesEnabled;
                Foliage.ApplyTo(settings.Foliage);
                SceneSubmission.ApplyTo(settings.SceneSubmission);
                AsyncCompute.ApplyTo(settings.AsyncCompute);
                settings.Diagnostics.GpuMeshletCountersEnabled = GpuMeshletCountersEnabled;
            }
        }

        private sealed record AsyncComputeFile
        {
            public bool Enabled { get; init; }
            public bool HiZBuildEnabled { get; init; } = true;
            public bool AmbientOcclusionBlurEnabled { get; init; } = true;
            public bool FogEnabled { get; init; } = true;
            public bool BloomEnabled { get; init; } = true;
            public bool GpuParticlesEnabled { get; init; } = true;

            public static AsyncComputeFile FromSettings(AsyncComputeSettings settings)
            {
                return new AsyncComputeFile
                {
                    Enabled = settings.Enabled,
                    HiZBuildEnabled = settings.HiZBuildEnabled,
                    AmbientOcclusionBlurEnabled = settings.AmbientOcclusionBlurEnabled,
                    FogEnabled = settings.FogEnabled,
                    BloomEnabled = settings.BloomEnabled,
                    GpuParticlesEnabled = settings.GpuParticlesEnabled
                };
            }

            public void ApplyTo(AsyncComputeSettings settings)
            {
                settings.Enabled = Enabled;
                settings.HiZBuildEnabled = HiZBuildEnabled;
                settings.AmbientOcclusionBlurEnabled = AmbientOcclusionBlurEnabled;
                settings.FogEnabled = FogEnabled;
                settings.BloomEnabled = BloomEnabled;
                settings.GpuParticlesEnabled = GpuParticlesEnabled;
            }
        }

        private sealed record SceneSubmissionFile
        {
            public bool GpuCompactionEnabled { get; init; }
            public bool IndirectMeshletDispatchEnabled { get; init; }
            public bool GpuLodSelectionEnabled { get; init; }
            public bool GpuShadowCompactionEnabled { get; init; }
            public bool ValidationCompareCpuGpuLists { get; init; }

            public static SceneSubmissionFile FromSettings(SceneSubmissionSettings settings)
            {
                return new SceneSubmissionFile
                {
                    GpuCompactionEnabled = settings.GpuCompactionEnabled,
                    IndirectMeshletDispatchEnabled = settings.IndirectMeshletDispatchEnabled,
                    GpuLodSelectionEnabled = settings.GpuLodSelectionEnabled,
                    GpuShadowCompactionEnabled = settings.GpuShadowCompactionEnabled,
                    ValidationCompareCpuGpuLists = settings.ValidationCompareCpuGpuLists
                };
            }

            public void ApplyTo(SceneSubmissionSettings settings)
            {
                settings.GpuCompactionEnabled = GpuCompactionEnabled;
                settings.IndirectMeshletDispatchEnabled = IndirectMeshletDispatchEnabled;
                settings.GpuLodSelectionEnabled = GpuLodSelectionEnabled;
                settings.GpuShadowCompactionEnabled = GpuShadowCompactionEnabled;
                settings.ValidationCompareCpuGpuLists = ValidationCompareCpuGpuLists;
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
            public bool MotionVectorsEnabled { get; init; }
            public bool LocalShadowsEnabled { get; init; }
            public float GrassShadowDistance { get; init; } = 25f;
            public float GrassShadowDensityScale { get; init; } = 0.5f;
            public float MaxDrawDistance { get; init; } = 250f;
            public float DensityScale { get; init; } = 1f;
            public int MaxVisibleClusters { get; init; } = 262144;
            public int MaxVisibleMeshletDraws { get; init; } = 524288;
            public int MaxLocalShadowedSpotLights { get; init; } = 1;
            public int MaxLocalShadowedPointLights { get; init; }
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
            public bool Enabled { get; init; }
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
