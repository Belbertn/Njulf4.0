namespace Njulf.Rendering.Data
{
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
        public BloomSettings Bloom { get; } = new();
    }
}
