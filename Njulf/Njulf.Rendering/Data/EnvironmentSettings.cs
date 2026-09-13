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
}
