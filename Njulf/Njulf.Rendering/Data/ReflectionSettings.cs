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
    public enum ReflectionDenoiser { Existing, Amd, Off }
    /// <summary>
    /// Selects the implementation used to execute the requested reflection
    /// mode. Auto deliberately resolves to Adaptive in production; Legacy is
    /// retained as an explicit rollback and comparison path.
    /// </summary>
    public enum ReflectionImplementationMode : uint
    {
        Auto = 0,
        Legacy = 1,
        Adaptive = 2
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

    public sealed class ReflectionSettings
    {
        public ReflectionDenoiser Denoiser { get; set; } = ReflectionDenoiser.Amd;
        /// <summary>Half-width/half-height AMD filtering; select native resolution for finer reflection detail.</summary>
        public bool AmdHalfResolution { get; set; } = true;
        private float _captureLodTargetPixelError = 1f;

        /// <summary>Select capture mesh LOD using error measured in capture pixels.</summary>
        public bool CaptureLodEnabled { get; set; } = true;
        public float CaptureLodTargetPixelError
        {
            get => _captureLodTargetPixelError;
            set => _captureLodTargetPixelError = float.IsFinite(value) ? Math.Clamp(value, 0.125f, 8f) : 1f;
        }

        internal uint CaptureLodSignature => BitConverter.SingleToUInt32Bits(CaptureLodTargetPixelError) |
            (CaptureLodEnabled ? 0x80000000u : 0u);

        internal static float CaptureLodErrorFor(RenderQualityPreset preset) => preset switch
        {
            RenderQualityPreset.Low => 4f,
            RenderQualityPreset.Medium => 2f,
            RenderQualityPreset.Ultra => 0.5f,
            _ => 1f
        };

        public const int ShaderMaxProbesPerPixel = 4;
        public const uint ReceiverPayloadAbiVersion = 5;
        public const uint HistoryMetadataAbiVersion = 2;

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
        public ReflectionImplementationMode ImplementationMode { get; set; } =
            ReflectionImplementationMode.Auto;

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
                    SpatialFilterPassCount = 1;
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
                    SpatialFilterPassCount = 2;
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
}
