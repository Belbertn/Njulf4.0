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
    public enum BloomDebugView : uint
    {
        None = 0,
        ExtractMask = 1,
        DownsampleMip = 2,
        UpsampleResult = 3,
        BloomOnly = 4
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

        public bool SmaaPredicationEnabled { get; set; } = true;
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
                AntiAliasingMode.SmaaHigh or
                AntiAliasingMode.SmaaUltra;
        }

        private static SmaaPreset GetSmaaPreset(AntiAliasingMode mode)
        {
            return mode switch
            {
                AntiAliasingMode.SmaaLow => new SmaaPreset(0, 0.15f, 4, 0, 0.0f),
                AntiAliasingMode.SmaaMedium => new SmaaPreset(1, 0.10f, 8, 0, 0.0f),
                AntiAliasingMode.SmaaHigh => new SmaaPreset(2, 0.10f, 16, 8, 25.0f),
                AntiAliasingMode.SmaaUltra => new SmaaPreset(3, 0.05f, 32, 16, 25.0f),
                _ => new SmaaPreset(0, 0.0f, 0, 0, 0.0f)
            };
        }

        private readonly struct SmaaPreset
        {
            public SmaaPreset(
                int quality,
                float threshold,
                int maxSearchSteps,
                int maxSearchStepsDiagonal,
                float cornerRounding)
            {
                Quality = quality;
                Threshold = threshold;
                MaxSearchSteps = maxSearchSteps;
                MaxSearchStepsDiagonal = maxSearchStepsDiagonal;
                CornerRounding = cornerRounding;
            }

            public int Quality { get; }
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
}
