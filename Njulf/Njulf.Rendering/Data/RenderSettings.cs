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
    }
}
