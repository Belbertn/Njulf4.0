using Njulf.Core.Math;

namespace Njulf.Rendering.Data
{
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
        private int _directionalCascadeCount = 3;
        private float _maxShadowDistance = 80f;
        private float _normalBias = 0.03f;
        private float _slopeScaledDepthBias = 1.5f;
        private float _constantDepthBias = 0.0005f;
        private int _pcfRadius = 1;
        private int _maxShadowedSpotLights = 8;
        private uint _spotShadowAtlasSize = 4096;
        private uint _spotShadowTileSize = 512;
        private float _spotNormalBias = 0.02f;
        private float _spotConstantDepthBias = 0.0005f;
        private float _spotSlopeScaledDepthBias = 1.5f;
        private int _spotPcfRadius = 1;
        private int _maxShadowedPointLights = 1;
        private uint _pointShadowMapSize = 512;
        private float _pointNormalBias = 0.03f;
        private float _pointConstantDepthBias = 0.001f;
        private float _pointSlopeScaledDepthBias = 1.5f;
        private int _pointPcfRadius = 1;

        public bool DirectionalShadowsEnabled { get; set; } = true;
        public bool SpotShadowsEnabled { get; set; } = true;
        public bool PointShadowsEnabled { get; set; } = true;

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

        private int _maxProbes = 64;
        private int _maxProbesPerPixel = 2;
        private uint _probeResolution = 256;
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

    public sealed class RenderSettings
    {
        private float _exposure = 1.0f;

        public float Exposure
        {
            get => _exposure;
            set => _exposure = value < 0.0f ? 0.0f : value;
        }

        public ToneMapper ToneMapper { get; set; } = ToneMapper.AcesFitted;
        public bool ShowRawHdrSceneColor { get; set; }
        public ShadowSettings Shadows { get; } = new();
        public BloomSettings Bloom { get; } = new();
        public EnvironmentSettings Environment { get; } = new();
        public ReflectionSettings Reflections { get; } = new();
        public AmbientOcclusionSettings AmbientOcclusion { get; } = new();
        public AntiAliasingSettings AntiAliasing { get; } = new();
        public FogSettings Fog { get; } = new();
    }
}
