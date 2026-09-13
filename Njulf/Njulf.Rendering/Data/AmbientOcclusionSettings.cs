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
}
