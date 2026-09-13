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
}
