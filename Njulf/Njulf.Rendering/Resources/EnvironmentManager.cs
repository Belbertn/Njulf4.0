using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using GpuVector4 = Njulf.Core.Math.Vector4;

namespace Njulf.Rendering.Resources
{
    public sealed unsafe class EnvironmentManager : IDisposable
    {
        private static readonly ulong EnvironmentDataSize = (ulong)Marshal.SizeOf<GPUEnvironmentData>();

        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly TextureManager _textureManager;
        private readonly RenderSettings _settings;

        private BufferHandle _environmentBuffer;
        private BufferHandle _prefilterEnvironmentBuffer;
        private BufferHandle _giEnvironmentBuffer;
        private TextureHandle _environmentCubemap;
        private TextureHandle _irradianceCubemap;
        private TextureHandle _prefilteredCubemap;
        private TextureHandle _nextPrefilteredCubemap;
        private TextureHandle _brdfLut;
        private ResourceSignature _resourceSignature;
        private uint _prefilteredMipCount;
        private ulong _estimatedBytes;
        private bool _usesFallback;
        private bool _usesAnalyticSky;
        private bool _prefilterReady;
        private float _prefilterBlend = 1.0f;
        private ImageView[][] _prefilterStorageViews = [[], []];
        private bool[][] _prefilterMipInitialized = [[], []];
        private int _prefilterReadTexture = -1;
        private int _prefilterNextTexture = -1;
        private int _prefilterBuildTexture;
        private uint _prefilterBuildMip;
        private bool _prefilterBuildActive;
        private bool _prefilterBuildSnapshotCaptured;
        private bool _prefilterTransitionActive;
        private int _prefilterTransitionFrame;
        private GPUEnvironmentData _prefilterSnapshotData;
        private bool _prefilterSnapshotUploadRequired;
        private uint _requestedSpecularEnvironmentGeneration;
        private uint _buildingSpecularEnvironmentGeneration;
        private uint _publishedSpecularEnvironmentGeneration;
        private uint _prefilterResourceGeneration;
        private bool _disposed;
        private readonly IProceduralSkyModel _proceduralSkyModel =
            new HosekWilkieSkyModel();
        private readonly ProceduralAtmosphereFrame _atmosphereFrame = new();
        private readonly ProceduralAtmosphereFrame _requestedGiAtmosphereFrame = new();
        private readonly ProceduralAtmosphereFrame _giAtmosphereFrame = new();
        private GiAtmosphereAdmissionController _giAdmissionController;
        private ulong _requestedGiLightingSignature;
        private ulong _giLightingSignature;
        private uint _giLightingGeneration;
        private bool _giUploadRequired = true;
        private LightHandle _derivedSunHandle;
        private Light _authoredSunRestore;
        private bool _derivedSunWasCreated;
        private LightHandle _derivedMoonHandle;
        private long _lastAtmosphereTimestamp;

        public EnvironmentManager(
            VulkanContext context,
            BufferManager bufferManager,
            TextureManager textureManager,
            RenderSettings settings)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _textureManager = textureManager ?? throw new ArgumentNullException(nameof(textureManager));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            _environmentBuffer = _bufferManager.CreateDeviceBuffer(
                EnvironmentDataSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                requireDeviceAddress: false,
                MemoryBudgetCategory.EnvironmentMaps,
                "Environment Data Buffer");
            _prefilterEnvironmentBuffer = _bufferManager.CreateDeviceBuffer(
                EnvironmentDataSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                requireDeviceAddress: false,
                MemoryBudgetCategory.EnvironmentMaps,
                "Environment Prefilter Snapshot Buffer");
            _giEnvironmentBuffer = _bufferManager.CreateDeviceBuffer(
                EnvironmentDataSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                requireDeviceAddress: false,
                MemoryBudgetCategory.EnvironmentMaps,
                "Stepped GI Environment Data Buffer");

            UpdateAtmosphereFrame(
                ToNumerics(_settings.Environment.DirectSunDirection),
                authoredSunRadiance: null);

            RecreateResources(CreateResourceSignature());
        }

        public bool UsesFallback => _usesFallback;
        public uint EnvironmentSize => _settings.Environment.EnvironmentSize;
        public uint IrradianceSize => _settings.Environment.IrradianceSize;
        public uint PrefilteredSize => _settings.Environment.PrefilteredSize;
        public uint PrefilteredMipCount => _prefilteredMipCount;
        public uint BrdfLutSize => _settings.Environment.BrdfLutSize;
        public ulong EstimatedBytes => _estimatedBytes;
        public int TextureResourceCount => _usesAnalyticSky ? 3 : 4;
        public Format EnvironmentFormat => ResolveEnvironmentFormat(_settings.Environment.TexturePrecision);
        public ulong EnvironmentMapBytes =>
            _settings.Environment.SourceKind == EnvironmentSourceKind.ProceduralSky
                ? 0UL
                : EstimateCubeBytes(EnvironmentSize, 1, EnvironmentFormat);
        public ulong IrradianceMapBytes =>
            _settings.Environment.SourceKind == EnvironmentSourceKind.ProceduralSky
                ? 0UL
                : EstimateCubeBytes(IrradianceSize, 1, EnvironmentFormat);
        public ulong PrefilteredEnvironmentBytes => EstimateCubeBytes(
            PrefilteredSize,
            _prefilteredMipCount,
            EnvironmentFormat) *
            (_settings.Environment.SourceKind == EnvironmentSourceKind.ProceduralSky ? 2UL : 1UL);
        public ulong BrdfLutBytes => checked((ulong)BrdfLutSize * BrdfLutSize * GetBytesPerPixel(EnvironmentFormat));
        public BufferHandle EnvironmentBuffer => _environmentBuffer;
        public BufferHandle PrefilterEnvironmentBuffer => _prefilterEnvironmentBuffer;
        public BufferHandle GiEnvironmentBuffer => _giEnvironmentBuffer;
        public ulong EnvironmentBufferBytes => checked(EnvironmentDataSize * 3UL);
        public bool UsesAnalyticSky => _usesAnalyticSky;
        public ulong GiLightingSignature => _giLightingSignature;
        public uint GiLightingGeneration => _giLightingGeneration;
        public ulong RequestedGiLightingSignature => _requestedGiLightingSignature;
        public uint RequestedGiLightingGeneration => _giAdmissionController.RequestedGeneration;
        public uint RequestedSpecularEnvironmentGeneration => _requestedSpecularEnvironmentGeneration;
        public uint PublishedSpecularEnvironmentGeneration => _publishedSpecularEnvironmentGeneration;
        public ulong GiCandidateRequestCount => _giAdmissionController.RequestedCount;
        public ulong GiCandidateCoalescedCount => _giAdmissionController.CoalescedCount;
        public ulong GiAdmissionCount => _giAdmissionController.AdmittedCount;
        public bool HasPendingGiAtmosphere => _giAdmissionController.HasPendingCandidate;
        internal ProceduralAtmosphereFrame AtmosphereFrame => _atmosphereFrame;
        internal ProceduralAtmosphereFrame GiAtmosphereFrame => _giAtmosphereFrame;

        /// <summary>
        /// The sampled images read by lighting and DDGI. Returning handles (rather than
        /// descriptor indices) lets the caller resolve lifetime generation and subresource
        /// ranges before constructing an ownership-transfer plan.
        /// </summary>
        public IReadOnlyList<TextureHandle> GetSampledTextureHandles()
        {
            var handles = new List<TextureHandle>(5);
            AddIfValid(handles, _environmentCubemap);
            AddIfValid(handles, _irradianceCubemap);
            AddIfValid(handles, _prefilteredCubemap);
            AddIfValid(handles, _nextPrefilteredCubemap);
            AddIfValid(handles, _brdfLut);
            return handles;
        }

        /// <summary>
        /// Updates the atmosphere and any derived sun/moon lights before the
        /// light buffer is uploaded. This is a per-frame coefficient update; it
        /// never recreates textures or waits for the device.
        /// </summary>
        public void UpdateFrameLighting(LightManager lightManager)
        {
            ArgumentNullException.ThrowIfNull(lightManager);
            EnvironmentSettings environment = _settings.Environment;
            AdvanceAstronomicalClock(environment);

            if (!environment.Enabled ||
                environment.SourceKind != EnvironmentSourceKind.ProceduralSky)
            {
                // This value is consumed only when the GPU environment buffer
                // is temporarily unavailable. Never retain analytic sky energy
                // after a scene disables or replaces the procedural source.
                environment.TransportFallbackRadiance = default;
                RestoreAuthoredLighting(lightManager);
                return;
            }

            if (environment.SunDriver == ProceduralSkySunDriver.SceneDirectionalLight)
            {
                RestoreAuthoredLighting(lightManager);
                LightFrameSnapshot snapshot = lightManager.GetFrameSnapshot();
                if (TryResolvePrimaryDirectionalLight(snapshot, out Light sun))
                {
                    Vector3 toSun = sun.Direction.LengthSquared() > 0.000001f
                        ? Vector3.Normalize(-sun.Direction)
                        : ToNumerics(environment.DirectSunDirection);
                    Vector3 radiance = Vector3.Max(sun.Color, Vector3.Zero) *
                        MathF.Max(sun.Intensity, 0.0f);
                    UpdateAtmosphereFrame(toSun, radiance);
                }
                else
                {
                    UpdateAtmosphereFrame(
                        ToNumerics(environment.DirectSunDirection),
                        Vector3.Zero);
                }
                return;
            }

            Vector3 derivedToSun = environment.SunDriver ==
                ProceduralSkySunDriver.AstronomicalTime
                    ? SolarPositionCalculator.CalculateToSunDirection(
                        environment.TimeOfDayHours,
                        environment.LatitudeDegrees,
                        environment.DayOfYear,
                        environment.NorthOffsetDegrees)
                    : ToNumerics(environment.DirectSunDirection);
            UpdateAtmosphereFrame(derivedToSun, authoredSunRadiance: null);
            ApplyDerivedLighting(lightManager);
        }

        private void UpdateAtmosphereFrame(
            Vector3 toSunDirection,
            Vector3? authoredSunRadiance)
        {
            _proceduralSkyModel.UpdateFrame(
                _settings.Environment,
                toSunDirection,
                authoredSunRadiance,
                _atmosphereFrame);

            UpdateRequestedGiAtmosphereFrame(toSunDirection, authoredSunRadiance);

            // Preserve one common, low-frequency safety fallback for code paths
            // that cannot consume the environment buffer (for example an early
            // DDGI initialization frame). The SH stores irradiance, so divide by
            // pi to recover an equivalent constant Lambertian incident radiance.
            // Use the stepped GI frame so exceptional transport paths obey the
            // same temporal source policy as normal probe misses.
            Vector3 irradiance = HosekWilkieSkyModel.EvaluateDiffuseIrradianceSh(
                Vector3.UnitY,
                _giAtmosphereFrame.DiffuseIrradianceSh);
            Vector3 fallback = Vector3.Max(irradiance / MathF.PI, Vector3.Zero);
            _settings.Environment.TransportFallbackRadiance = new Njulf.Core.Math.Vector3(
                fallback.X,
                fallback.Y,
                fallback.Z);
        }

        private void UpdateRequestedGiAtmosphereFrame(
            Vector3 toSunDirection,
            Vector3? authoredSunRadiance)
        {
            EnvironmentSettings settings = _settings.Environment;
            Vector3 steppedToSun = QuantizeGiSunDirection(
                toSunDirection,
                settings.GiSunStepDegrees);
            Vector3? steppedAuthoredRadiance = authoredSunRadiance.HasValue
                ? QuantizeRadiance(authoredSunRadiance.Value, 0.01f)
                : null;
            ulong signature = CreateGiAtmosphereSignature(
                settings,
                steppedToSun,
                steppedAuthoredRadiance);
            if (signature == _requestedGiLightingSignature)
                return;

            _proceduralSkyModel.UpdateFrame(
                settings,
                steppedToSun,
                steppedAuthoredRadiance,
                _requestedGiAtmosphereFrame);
            _requestedGiLightingSignature = signature;
            _requestedSpecularEnvironmentGeneration = AdvanceGeneration(_requestedSpecularEnvironmentGeneration);

            // Construction and non-DDGI callers still receive a valid first snapshot. Subsequent
            // changes cross the explicit renderer-owned admission boundary.
            if (_giLightingGeneration == 0u)
                ApplyGiAtmosphereAdmission(default);
        }

        public GiAtmosphereAdmissionDecision ApplyGiAtmosphereAdmission(
            in GiAtmosphereCohortFeedback cohort,
            bool hardInvalidation = false,
            uint currentVolumeResourceGeneration = 0U,
            uint currentSourceCohortGeneration = 0U,
            uint currentPropagationGeneration = 0U)
        {
            if (_requestedGiLightingSignature == 0UL)
                return default;

            GiAtmosphereAdmissionDecision decision = _giAdmissionController.Update(
                new GiAtmosphereAdmissionInput(
                    _requestedGiLightingSignature,
                    cohort,
                    hardInvalidation,
                    currentVolumeResourceGeneration,
                    currentSourceCohortGeneration,
                    currentPropagationGeneration));
            if (decision.Action is not (GiAtmosphereAdmissionAction.AdmitPendingCandidate or
                GiAtmosphereAdmissionAction.HardRestartWithCandidate) ||
                (decision.Action != GiAtmosphereAdmissionAction.HardRestartWithCandidate &&
                 decision.AdmittedSignature == _giLightingSignature))
            {
                return decision;
            }

            CopyAtmosphereFrame(_requestedGiAtmosphereFrame, _giAtmosphereFrame);
            _giLightingSignature = decision.AdmittedSignature;
            _giLightingGeneration = decision.AdmittedGeneration;
            _giUploadRequired = true;
            UpdateTransportFallbackRadiance();
            return decision;
        }

        private void UpdateTransportFallbackRadiance()
        {
            Vector3 irradiance = HosekWilkieSkyModel.EvaluateDiffuseIrradianceSh(
                Vector3.UnitY,
                _giAtmosphereFrame.DiffuseIrradianceSh);
            Vector3 fallback = Vector3.Max(irradiance / MathF.PI, Vector3.Zero);
            _settings.Environment.TransportFallbackRadiance = new Njulf.Core.Math.Vector3(
                fallback.X, fallback.Y, fallback.Z);
        }

        private static void CopyAtmosphereFrame(ProceduralAtmosphereFrame source, ProceduralAtmosphereFrame destination)
        {
            source.HosekParameters.AsSpan().CopyTo(destination.HosekParameters);
            source.HosekRadiances.AsSpan().CopyTo(destination.HosekRadiances);
            source.DiffuseIrradianceSh.AsSpan().CopyTo(destination.DiffuseIrradianceSh);
            destination.ToSunDirection = source.ToSunDirection;
            destination.SunRadiance = source.SunRadiance;
            destination.ToMoonDirection = source.ToMoonDirection;
            destination.MoonRadiance = source.MoonRadiance;
            destination.GroundAlbedo = source.GroundAlbedo;
            destination.GroundRadiance = source.GroundRadiance;
            destination.SunAngularRadiusRadians = source.SunAngularRadiusRadians;
            destination.MoonAngularRadiusRadians = source.MoonAngularRadiusRadians;
            destination.SunElevationRadians = source.SunElevationRadians;
            destination.Turbidity = source.Turbidity;
            destination.AtmosphereIntensity = source.AtmosphereIntensity;
            destination.DayBlend = source.DayBlend;
            destination.TwilightBlend = source.TwilightBlend;
            destination.NightBlend = source.NightBlend;
            destination.StarIntensity = source.StarIntensity;
            destination.AirglowIntensity = source.AirglowIntensity;
            destination.SourceSignature = source.SourceSignature;
            destination.Revision = source.Revision;
        }

        private static uint AdvanceGeneration(uint generation) =>
            generation == uint.MaxValue ? 1u : generation + 1u;

        internal static Vector3 QuantizeGiSunDirection(
            Vector3 toSunDirection,
            float stepDegrees)
        {
            Vector3 direction = toSunDirection.LengthSquared() > 0.000001f
                ? Vector3.Normalize(toSunDirection)
                : Vector3.UnitY;
            float safeStep = Math.Clamp(stepDegrees, 0.02f, 5.0f);
            float elevation = MathF.Asin(Math.Clamp(direction.Y, -1.0f, 1.0f));
            float azimuth = MathF.Atan2(direction.X, direction.Z);
            float radiansPerStep = safeStep * MathF.PI / 180.0f;
            elevation = Math.Clamp(
                MathF.Round(elevation / radiansPerStep) * radiansPerStep,
                -MathF.PI * 0.5f,
                MathF.PI * 0.5f);
            azimuth = MathF.Round(azimuth / radiansPerStep) * radiansPerStep;
            float horizontal = MathF.Cos(elevation);
            return Vector3.Normalize(new Vector3(
                MathF.Sin(azimuth) * horizontal,
                MathF.Sin(elevation),
                MathF.Cos(azimuth) * horizontal));
        }

        private static Vector3 QuantizeRadiance(Vector3 radiance, float step)
        {
            Vector3 safe = Vector3.Max(radiance, Vector3.Zero);
            return new Vector3(
                MathF.Round(safe.X / step) * step,
                MathF.Round(safe.Y / step) * step,
                MathF.Round(safe.Z / step) * step);
        }

        private static ulong CreateGiAtmosphereSignature(
            EnvironmentSettings settings,
            Vector3 steppedToSun,
            Vector3? steppedAuthoredRadiance)
        {
            const ulong offsetBasis = 14695981039346656037UL;
            ulong hash = offsetBasis;
            hash = HashGiValue(hash, settings.Enabled ? 1u : 0u);
            hash = HashGiValue(hash, (uint)settings.SourceKind);
            hash = HashGiValue(hash, (uint)settings.SunDriver);
            hash = HashGiValue(hash, steppedToSun);
            hash = HashGiValue(hash, steppedAuthoredRadiance ?? new Vector3(-1.0f));
            hash = HashGiValue(hash, settings.Turbidity);
            hash = HashGiValue(hash, new Vector3(
                settings.GroundAlbedo.X,
                settings.GroundAlbedo.Y,
                settings.GroundAlbedo.Z));
            hash = HashGiValue(hash, settings.SunAngularDiameterDegrees);
            hash = HashGiValue(hash, settings.MoonAngularDiameterDegrees);
            hash = HashGiValue(hash, settings.AtmosphereIntensity);
            hash = HashGiValue(hash, settings.SolarIrradianceScale);
            hash = HashGiValue(hash, settings.MoonIrradianceScale);
            hash = HashGiValue(hash, settings.StarIntensity);
            hash = HashGiValue(hash, settings.AirglowIntensity);
            hash = HashGiValue(hash, settings.SkyIntensity);
            hash = HashGiValue(hash, settings.RotationRadians);
            return hash;
        }

        private static ulong HashGiValue(ulong hash, Vector3 value)
        {
            hash = HashGiValue(hash, BitConverter.SingleToUInt32Bits(value.X));
            hash = HashGiValue(hash, BitConverter.SingleToUInt32Bits(value.Y));
            return HashGiValue(hash, BitConverter.SingleToUInt32Bits(value.Z));
        }

        private static ulong HashGiValue(ulong hash, float value) =>
            HashGiValue(hash, BitConverter.SingleToUInt32Bits(value));

        private static ulong HashGiValue(ulong hash, uint value)
        {
            const ulong prime = 1099511628211UL;
            hash ^= value;
            return hash * prime;
        }

        private void AdvanceAstronomicalClock(EnvironmentSettings environment)
        {
            long now = Stopwatch.GetTimestamp();
            if (_lastAtmosphereTimestamp == 0)
            {
                _lastAtmosphereTimestamp = now;
                return;
            }

            float elapsedSeconds = (float)Math.Min(
                Stopwatch.GetElapsedTime(_lastAtmosphereTimestamp, now).TotalSeconds,
                0.25);
            _lastAtmosphereTimestamp = now;
            if (!environment.AnimateTimeOfDay ||
                environment.SunDriver != ProceduralSkySunDriver.AstronomicalTime)
            {
                return;
            }

            float hours = environment.TimeOfDayHours +
                elapsedSeconds * environment.TimeScale / 3600.0f;
            hours %= 24.0f;
            if (hours < 0.0f)
                hours += 24.0f;
            environment.TimeOfDayHours = hours;
        }

        private void ApplyDerivedLighting(LightManager lightManager)
        {
            if (!_derivedSunHandle.IsValid ||
                !lightManager.TryGetLight(_derivedSunHandle, out Light sun))
            {
                LightFrameSnapshot snapshot = lightManager.GetFrameSnapshot();
                if (TryResolvePrimaryDirectionalLight(snapshot, out sun, out int packedIndex) &&
                    lightManager.TryGetLightHandle(packedIndex, out _derivedSunHandle))
                {
                    _authoredSunRestore = sun;
                    _derivedSunWasCreated = false;
                }
                else
                {
                    sun = new Light
                    {
                        Type = LightType.Directional,
                        CastsShadows = true,
                        ShadowStrength = 1.0f,
                        ShadowPriority = 10,
                        Range = 10.0f
                    };
                    _derivedSunHandle = lightManager.AddLightHandle(
                        sun,
                        "Procedural Atmosphere Sun");
                    _derivedSunWasCreated = true;
                }
            }

            Light previousSun = sun;
            DecomposeRadiance(
                _atmosphereFrame.SunRadiance,
                out sun.Color,
                out sun.Intensity);
            sun.Type = LightType.Directional;
            sun.Direction = -_atmosphereFrame.ToSunDirection;
            if (!LightsApproximatelyEqual(previousSun, sun))
                lightManager.UpdateLight(_derivedSunHandle, sun);

            if (!_derivedMoonHandle.IsValid ||
                !lightManager.TryGetLight(_derivedMoonHandle, out Light moon))
            {
                moon = new Light
                {
                    Type = LightType.Directional,
                    CastsShadows = false,
                    ShadowStrength = 0.0f,
                    ShadowPriority = -10,
                    Range = 10.0f
                };
                _derivedMoonHandle = lightManager.AddLightHandle(
                    moon,
                    "Procedural Atmosphere Moon");
            }

            Light previousMoon = moon;
            DecomposeRadiance(
                _atmosphereFrame.MoonRadiance,
                out moon.Color,
                out moon.Intensity);
            moon.Type = LightType.Directional;
            moon.Direction = -_atmosphereFrame.ToMoonDirection;
            moon.CastsShadows = false;
            if (!LightsApproximatelyEqual(previousMoon, moon))
                lightManager.UpdateLight(_derivedMoonHandle, moon);
        }

        private static bool LightsApproximatelyEqual(in Light left, in Light right)
        {
            const float epsilon = 1.0e-6f;
            return Vector3.DistanceSquared(left.Position, right.Position) <= epsilon * epsilon &&
                MathF.Abs(left.Intensity - right.Intensity) <= epsilon &&
                Vector3.DistanceSquared(left.Color, right.Color) <= epsilon * epsilon &&
                MathF.Abs(left.Range - right.Range) <= epsilon &&
                Vector3.DistanceSquared(left.Direction, right.Direction) <= epsilon * epsilon &&
                MathF.Abs(left.SpotAngle - right.SpotAngle) <= epsilon &&
                MathF.Abs(left.InnerSpotAngle - right.InnerSpotAngle) <= epsilon &&
                left.AttenuationMode == right.AttenuationMode &&
                MathF.Abs(left.AttenuationConstant - right.AttenuationConstant) <= epsilon &&
                MathF.Abs(left.AttenuationLinear - right.AttenuationLinear) <= epsilon &&
                MathF.Abs(left.AttenuationQuadratic - right.AttenuationQuadratic) <= epsilon &&
                left.Type == right.Type &&
                left.CastsShadows == right.CastsShadows &&
                MathF.Abs(left.ShadowStrength - right.ShadowStrength) <= epsilon &&
                left.ShadowMapSizeOverride == right.ShadowMapSizeOverride &&
                MathF.Abs(left.ShadowNearPlane - right.ShadowNearPlane) <= epsilon &&
                MathF.Abs(left.ShadowFarPlane - right.ShadowFarPlane) <= epsilon &&
                left.ShadowPriority == right.ShadowPriority;
        }

        private void RestoreAuthoredLighting(LightManager lightManager)
        {
            if (_derivedMoonHandle.IsValid)
            {
                lightManager.RemoveLight(_derivedMoonHandle);
                _derivedMoonHandle = default;
            }

            if (!_derivedSunHandle.IsValid)
                return;
            if (_derivedSunWasCreated)
                lightManager.RemoveLight(_derivedSunHandle);
            else
                lightManager.UpdateLight(_derivedSunHandle, _authoredSunRestore);
            _derivedSunHandle = default;
            _derivedSunWasCreated = false;
        }

        private static bool TryResolvePrimaryDirectionalLight(
            in LightFrameSnapshot snapshot,
            out Light light) =>
            TryResolvePrimaryDirectionalLight(snapshot, out light, out _);

        private static bool TryResolvePrimaryDirectionalLight(
            in LightFrameSnapshot snapshot,
            out Light light,
            out int packedIndex)
        {
            if (snapshot.HasShadowCastingDirectionalLight)
            {
                light = snapshot.FirstShadowCastingDirectionalLight;
                packedIndex = snapshot.FirstShadowCastingDirectionalLightIndex;
                return true;
            }

            ReadOnlySpan<Light> lights = snapshot.Lights.Span;
            int count = Math.Min(snapshot.Count, lights.Length);
            for (int index = 0; index < count; index++)
            {
                if (lights[index].Type != LightType.Directional)
                    continue;
                light = lights[index];
                packedIndex = index;
                return true;
            }

            light = default;
            packedIndex = -1;
            return false;
        }

        private static void DecomposeRadiance(
            Vector3 radiance,
            out Vector3 color,
            out float intensity)
        {
            Vector3 safe = Vector3.Max(radiance, Vector3.Zero);
            intensity = MathF.Max(safe.X, MathF.Max(safe.Y, safe.Z));
            color = intensity > 0.000001f ? safe / intensity : Vector3.Zero;
        }

        internal bool IsManagedAtmosphereLight(int packedIndex, LightManager lightManager)
        {
            if (packedIndex < 0 ||
                !lightManager.TryGetLightHandle(packedIndex, out LightHandle handle))
            {
                return false;
            }

            return handle == _derivedSunHandle || handle == _derivedMoonHandle;
        }

        public void EnsureResourcesCurrent(BindlessHeap? bindlessHeap = null, Action? waitIdle = null)
        {
            ResourceSignature signature = CreateResourceSignature();
            if (signature.Equals(_resourceSignature))
                return;

            if (waitIdle != null)
                waitIdle();
            else
                _context.WaitIdle();
            RecreateResources(signature);
            if (bindlessHeap != null)
                RegisterReflectionProbeFallback(bindlessHeap);
        }

        public void Register(BindlessHeap bindlessHeap)
        {
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));

            bindlessHeap.RegisterStorageBuffer(
                BindlessIndex.EnvironmentDataBuffer,
                _bufferManager.GetBuffer(_environmentBuffer),
                0,
                EnvironmentDataSize);
            bindlessHeap.RegisterStorageBuffer(
                BindlessIndex.EnvironmentPrefilterDataBuffer,
                _bufferManager.GetBuffer(_prefilterEnvironmentBuffer),
                0,
                EnvironmentDataSize);
            bindlessHeap.RegisterStorageBuffer(
                BindlessIndex.EnvironmentGiDataBuffer,
                _bufferManager.GetBuffer(_giEnvironmentBuffer),
                0,
                EnvironmentDataSize);
        }

        public void RegisterReflectionProbeFallback(BindlessHeap bindlessHeap)
        {
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));

            ImageView prefilteredView = _textureManager.GetTextureView(_prefilteredCubemap);
            bindlessHeap.RegisterTexture(BindlessIndex.ReflectionProbeCubemapArrayTexture, prefilteredView);
            bindlessHeap.RegisterTexture(BindlessIndex.ReflectionProbeDebugTexture, prefilteredView);
            if (_nextPrefilteredCubemap.IsValid)
            {
                bindlessHeap.RegisterTexture(
                    BindlessIndex.PrefilteredEnvironmentNextTexture,
                    _textureManager.GetTextureView(_nextPrefilteredCubemap));
            }
        }

        public void Upload(StagingRing stagingRing, CommandBuffer commandBuffer)
        {
            if (stagingRing == null)
                throw new ArgumentNullException(nameof(stagingRing));
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required for environment upload.", nameof(commandBuffer));

            _settings.Environment.ClampDebugMipLevel(_prefilteredMipCount);
            AdvancePrefilterStateBeforeUpload();
            GPUEnvironmentData data = CreateGpuData(_atmosphereFrame);
            if (_usesAnalyticSky &&
                _prefilterBuildActive &&
                !_prefilterBuildSnapshotCaptured)
            {
                _prefilterSnapshotData = data;
                _buildingSpecularEnvironmentGeneration = _requestedSpecularEnvironmentGeneration;
                _prefilterBuildSnapshotCaptured = true;
                _prefilterSnapshotUploadRequired = true;
            }
            GpuBufferUploader.UploadValueToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                _environmentBuffer,
                data,
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.ShaderStorageReadBit,
                    size: EnvironmentDataSize));
            if (!_usesAnalyticSky || _prefilterSnapshotUploadRequired)
            {
                GPUEnvironmentData snapshot = _usesAnalyticSky
                    ? _prefilterSnapshotData
                    : data;
                GpuBufferUploader.UploadValueToBuffer(
                    _context,
                    _bufferManager,
                    stagingRing,
                    commandBuffer,
                    _prefilterEnvironmentBuffer,
                    snapshot,
                    barrierDescription: new UploadBarrierDescription(
                        PipelineStageFlags2.ComputeShaderBit,
                        AccessFlags2.ShaderStorageReadBit,
                        size: EnvironmentDataSize));
                _prefilterSnapshotUploadRequired = false;
            }
            if (_giUploadRequired || !_usesAnalyticSky)
            {
                GPUEnvironmentData giData = _usesAnalyticSky
                    ? CreateGpuData(_giAtmosphereFrame)
                    : data;
                GpuBufferUploader.UploadValueToBuffer(
                    _context,
                    _bufferManager,
                    stagingRing,
                    commandBuffer,
                    _giEnvironmentBuffer,
                    giData,
                    barrierDescription: new UploadBarrierDescription(
                        PipelineStageFlags2.ComputeShaderBit,
                        AccessFlags2.ShaderStorageReadBit,
                        size: EnvironmentDataSize));
                _giUploadRequired = false;
            }
        }

        internal int PrefilterMipsPerFrame =>
            _settings.Environment.SpecularPrefilterMipsPerFrame;

        internal bool HasPendingPrefilterWork =>
            _usesAnalyticSky && _prefilterBuildActive;

        internal uint PrefilterResourceGeneration => _prefilterResourceGeneration;

        internal bool TryGetNextPrefilterWork(out EnvironmentPrefilterWork work)
        {
            if (!_usesAnalyticSky ||
                !_prefilterBuildActive ||
                !_prefilterBuildSnapshotCaptured ||
                _prefilterBuildMip >= _prefilteredMipCount ||
                _prefilterBuildTexture is < 0 or > 1)
            {
                work = default;
                return false;
            }

            TextureHandle texture = _prefilterBuildTexture == 0
                ? _prefilteredCubemap
                : _nextPrefilteredCubemap;
            if (!texture.IsValid ||
                !_textureManager.TryGetImageBinding(texture, out TextureImageBinding binding))
            {
                work = default;
                return false;
            }

            uint mip = _prefilterBuildMip;
            bool initialized = _prefilterMipInitialized[_prefilterBuildTexture][mip];
            work = new EnvironmentPrefilterWork(
                _prefilterResourceGeneration,
                _prefilterBuildTexture,
                mip,
                Math.Max(1u, _settings.Environment.PrefilteredSize >> checked((int)mip)),
                _prefilteredMipCount <= 1
                    ? 0.0f
                    : mip / (float)(_prefilteredMipCount - 1u),
                binding.Image,
                _prefilterStorageViews[_prefilterBuildTexture][mip],
                binding.Format,
                initialized ? ImageLayout.ShaderReadOnlyOptimal : ImageLayout.Undefined);
            return true;
        }

        internal void CompletePrefilterWork(in EnvironmentPrefilterWork work)
        {
            if (!_prefilterBuildActive ||
                work.ResourceGeneration != _prefilterResourceGeneration ||
                work.TargetTexture != _prefilterBuildTexture ||
                work.MipLevel != _prefilterBuildMip)
            {
                return;
            }

            _prefilterMipInitialized[work.TargetTexture][work.MipLevel] = true;
            _prefilterBuildMip++;
            if (_prefilterBuildMip < _prefilteredMipCount)
                return;

            _prefilterBuildActive = false;
            if (_prefilterReadTexture < 0)
            {
                _prefilterReadTexture = _prefilterBuildTexture;
                _prefilterNextTexture = _prefilterReadTexture;
                _publishedSpecularEnvironmentGeneration =
                    _buildingSpecularEnvironmentGeneration;
                _prefilterReady = true;
                _prefilterBlend = 1.0f;
                return;
            }

            _prefilterNextTexture = _prefilterBuildTexture;
            _prefilterTransitionActive = true;
            _prefilterTransitionFrame = 0;
            _prefilterBlend = 0.0f;
        }

        private void AdvancePrefilterStateBeforeUpload()
        {
            if (!_usesAnalyticSky)
                return;

            if (_prefilterTransitionActive)
            {
                int transitionFrames = Math.Max(
                    _settings.Environment.SpecularPrefilterTransitionFrames,
                    1);
                _prefilterTransitionFrame++;
                _prefilterBlend = Math.Clamp(
                    _prefilterTransitionFrame / (float)transitionFrames,
                    0.0f,
                    1.0f);
                if (_prefilterTransitionFrame >= transitionFrames)
                {
                    _prefilterReadTexture = _prefilterNextTexture;
                    _prefilterNextTexture = _prefilterReadTexture;
                    _publishedSpecularEnvironmentGeneration =
                        _buildingSpecularEnvironmentGeneration;
                    _prefilterBlend = 1.0f;
                    _prefilterTransitionActive = false;
                }
            }

            if (_prefilterBuildActive || _prefilterTransitionActive)
                return;

            // Specular IBL tracks the latest visual atmosphere independently of a held DDGI
            // cohort. A build pins its immutable snapshot until the whole mip chain completes.
            if (_requestedSpecularEnvironmentGeneration == 0u ||
                _requestedSpecularEnvironmentGeneration == _publishedSpecularEnvironmentGeneration)
            {
                return;
            }

            _prefilterBuildTexture = _prefilterReadTexture == 0 ? 1 : 0;
            _prefilterBuildMip = 0;
            _prefilterBuildActive = true;
            _prefilterBuildSnapshotCaptured = false;
        }

        private static int ResolvePrefilterTextureIndex(int texture) =>
            texture == 0
                ? BindlessIndex.PrefilteredEnvironmentTexture
                : BindlessIndex.PrefilteredEnvironmentNextTexture;

        private GPUEnvironmentData CreateGpuData(ProceduralAtmosphereFrame atmosphereFrame)
        {
            return new GPUEnvironmentData
            {
                EnvironmentTextureIndex = _usesAnalyticSky
                    ? -1
                    : BindlessIndex.EnvironmentCubemapTexture,
                IrradianceTextureIndex = _usesAnalyticSky
                    ? -1
                    : BindlessIndex.IrradianceCubemapTexture,
                PrefilteredTextureIndex = _usesAnalyticSky && _prefilterReadTexture >= 0
                    ? ResolvePrefilterTextureIndex(_prefilterReadTexture)
                    : BindlessIndex.PrefilteredEnvironmentTexture,
                BrdfLutTextureIndex = BindlessIndex.BrdfLutTexture,
                SkyIntensity = _settings.Environment.SkyIntensity,
                DiffuseIntensity = _settings.Environment.DiffuseIntensity,
                SpecularIntensity = _settings.Environment.SpecularIntensity,
                RotationRadians = _settings.Environment.RotationRadians,
                PrefilteredMipCount = _prefilteredMipCount,
                Enabled = _settings.Environment.Enabled ? 1u : 0u,
                DebugView = (uint)_settings.Environment.DebugView,
                DebugMipLevel = (uint)_settings.Environment.DebugMipLevel,
                NextPrefilteredTextureIndex = _usesAnalyticSky &&
                    _prefilterNextTexture >= 0
                        ? ResolvePrefilterTextureIndex(_prefilterNextTexture)
                        : (_usesAnalyticSky && _prefilterReadTexture >= 0
                            ? ResolvePrefilterTextureIndex(_prefilterReadTexture)
                            : BindlessIndex.PrefilteredEnvironmentTexture),
                SourceKind = (uint)_settings.Environment.SourceKind,
                AtmosphereFlags = (_usesAnalyticSky ? 1u : 0u) |
                    (_prefilterReady ? 2u : 0u),
                PrefilteredBlend = _prefilterBlend,
                SunDirectionAndAngularRadius = Pack(
                    atmosphereFrame.ToSunDirection,
                    atmosphereFrame.SunAngularRadiusRadians),
                SunRadianceAndElevation = Pack(
                    atmosphereFrame.SunRadiance,
                    atmosphereFrame.SunElevationRadians),
                MoonDirectionAndAngularRadius = Pack(
                    atmosphereFrame.ToMoonDirection,
                    atmosphereFrame.MoonAngularRadiusRadians),
                MoonRadianceAndNightBlend = Pack(
                    atmosphereFrame.MoonRadiance,
                    atmosphereFrame.NightBlend),
                GroundAlbedoAndTurbidity = Pack(
                    atmosphereFrame.GroundAlbedo,
                    atmosphereFrame.Turbidity),
                AtmosphereParameters = new GpuVector4(
                    atmosphereFrame.AtmosphereIntensity,
                    atmosphereFrame.DayBlend,
                    atmosphereFrame.TwilightBlend,
                    atmosphereFrame.StarIntensity),
                GroundRadianceAndAirglow = Pack(
                    atmosphereFrame.GroundRadiance,
                    atmosphereFrame.AirglowIntensity),
                HosekParametersR0 = Pack(atmosphereFrame.HosekParameters, 0),
                HosekParametersR1 = Pack(atmosphereFrame.HosekParameters, 4),
                HosekParametersR2 = PackLast(atmosphereFrame.HosekParameters, 8),
                HosekParametersG0 = Pack(atmosphereFrame.HosekParameters, 9),
                HosekParametersG1 = Pack(atmosphereFrame.HosekParameters, 13),
                HosekParametersG2 = PackLast(atmosphereFrame.HosekParameters, 17),
                HosekParametersB0 = Pack(atmosphereFrame.HosekParameters, 18),
                HosekParametersB1 = Pack(atmosphereFrame.HosekParameters, 22),
                HosekParametersB2 = PackLast(atmosphereFrame.HosekParameters, 26),
                HosekRadiances = Pack(
                    new Vector3(
                        atmosphereFrame.HosekRadiances[0],
                        atmosphereFrame.HosekRadiances[1],
                        atmosphereFrame.HosekRadiances[2]),
                    0.0f),
                DiffuseIrradianceSh0 = Pack(atmosphereFrame.DiffuseIrradianceSh[0], 0.0f),
                DiffuseIrradianceSh1 = Pack(atmosphereFrame.DiffuseIrradianceSh[1], 0.0f),
                DiffuseIrradianceSh2 = Pack(atmosphereFrame.DiffuseIrradianceSh[2], 0.0f),
                DiffuseIrradianceSh3 = Pack(atmosphereFrame.DiffuseIrradianceSh[3], 0.0f),
                DiffuseIrradianceSh4 = Pack(atmosphereFrame.DiffuseIrradianceSh[4], 0.0f),
                DiffuseIrradianceSh5 = Pack(atmosphereFrame.DiffuseIrradianceSh[5], 0.0f),
                DiffuseIrradianceSh6 = Pack(atmosphereFrame.DiffuseIrradianceSh[6], 0.0f),
                DiffuseIrradianceSh7 = Pack(atmosphereFrame.DiffuseIrradianceSh[7], 0.0f),
                DiffuseIrradianceSh8 = Pack(atmosphereFrame.DiffuseIrradianceSh[8], 0.0f)
            };
        }

        private void RecreateResources(ResourceSignature signature)
        {
            DestroyEnvironmentTextures();

            uint prefilteredSize = signature.PrefilteredSize;
            uint brdfSize = signature.BrdfLutSize;
            Format environmentFormat = ResolveEnvironmentFormat(signature.TexturePrecision);
            bool useAnalyticSky = signature.SourceKind ==
                EnvironmentSourceKind.ProceduralSky;
            bool fallback = false;
            EnvironmentPayload payload = default;
            if (signature.SourceKind == EnvironmentSourceKind.HdrEquirectangular)
            {
                try
                {
                    payload = CreateHdrEnvironmentPayload(
                        signature,
                        CalculateMipLevels(prefilteredSize, prefilteredSize));
                }
                catch (Exception ex) when (ex is IOException or
                    InvalidDataException or
                    UnauthorizedAccessException or
                    ArgumentException)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Environment HDR load failed for '{signature.ResolvedSourcePath}': " +
                        $"{ex.Message}. Using the analytic procedural atmosphere.");
                    useAnalyticSky = true;
                    fallback = true;
                }
            }

            if (useAnalyticSky)
            {
                _prefilteredMipCount = Math.Min(
                    5u,
                    CalculateMipLevels(prefilteredSize, prefilteredSize));
                _prefilteredCubemap = _textureManager.CreateCubemap(
                    prefilteredSize,
                    environmentFormat,
                    mipLevels: _prefilteredMipCount,
                    additionalUsage: ImageUsageFlags.StorageBit,
                    bindlessIndex: BindlessIndex.PrefilteredEnvironmentTexture,
                    debugName: "Procedural Environment Prefilter A");
                _nextPrefilteredCubemap = _textureManager.CreateCubemap(
                    prefilteredSize,
                    environmentFormat,
                    mipLevels: _prefilteredMipCount,
                    additionalUsage: ImageUsageFlags.StorageBit,
                    bindlessIndex: BindlessIndex.PrefilteredEnvironmentNextTexture,
                    debugName: "Procedural Environment Prefilter B");
                InitializeAnalyticPrefilterState();
                _prefilterReady = false;
                _prefilterBlend = 1.0f;
            }
            else
            {
                ResetPrefilterState();
                _prefilteredMipCount = CalculateMipLevels(
                    prefilteredSize,
                    prefilteredSize);
                payload = payload.ConvertToFormat(environmentFormat);
                CreateBakedEnvironmentTextures(
                    signature,
                    environmentFormat,
                    payload);
                _prefilterReady = true;
                _prefilterBlend = 1.0f;
            }

            _brdfLut = _textureManager.CreateTexture(
                brdfSize,
                brdfSize,
                environmentFormat,
                mipLevels: 1,
                bindlessIndex: BindlessIndex.BrdfLutTexture,
                debugName: "BRDF LUT Texture");
            _textureManager.UploadTextureData(
                _brdfLut,
                ConvertRgbaFloat32Payload(GenerateBrdfLut(brdfSize), environmentFormat),
                brdfSize,
                brdfSize,
                environmentFormat);

            _estimatedBytes =
                (useAnalyticSky
                    ? 2UL * EstimateCubeBytes(
                        prefilteredSize,
                        _prefilteredMipCount,
                        environmentFormat)
                    : EstimateCubeBytes(signature.EnvironmentSize, 1, environmentFormat) +
                        EstimateCubeBytes(signature.IrradianceSize, 1, environmentFormat) +
                        EstimateCubeBytes(
                            prefilteredSize,
                            _prefilteredMipCount,
                            environmentFormat)) +
                checked((ulong)brdfSize * brdfSize * GetBytesPerPixel(environmentFormat));

            _resourceSignature = signature;
            _usesAnalyticSky = useAnalyticSky;
            _usesFallback = fallback;
        }

        private void InitializeAnalyticPrefilterState()
        {
            int mipCount = checked((int)_prefilteredMipCount);
            _prefilterStorageViews =
            [
                new ImageView[mipCount],
                new ImageView[mipCount]
            ];
            _prefilterMipInitialized =
            [
                new bool[mipCount],
                new bool[mipCount]
            ];
            TextureHandle[] textures =
            [
                _prefilteredCubemap,
                _nextPrefilteredCubemap
            ];
            for (int texture = 0; texture < textures.Length; texture++)
            {
                for (uint mip = 0; mip < _prefilteredMipCount; mip++)
                {
                    _prefilterStorageViews[texture][mip] =
                        _textureManager.CreateTextureSubresourceView(
                            textures[texture],
                            mip,
                            levelCount: 1,
                            baseArrayLayer: 0,
                            layerCount: 6,
                            ImageViewType.Type2DArray);
                }
            }

            _prefilterReadTexture = -1;
            _prefilterNextTexture = -1;
            _prefilterBuildTexture = 0;
            _prefilterBuildMip = 0;
            _prefilterBuildActive = true;
            _prefilterBuildSnapshotCaptured = false;
            _prefilterSnapshotUploadRequired = false;
            _buildingSpecularEnvironmentGeneration = 0u;
            _publishedSpecularEnvironmentGeneration = 0u;
            _prefilterTransitionActive = false;
            _prefilterTransitionFrame = 0;
            _prefilterResourceGeneration++;
            if (_prefilterResourceGeneration == 0)
                _prefilterResourceGeneration = 1;
        }

        private void ResetPrefilterState()
        {
            _prefilterStorageViews = [[], []];
            _prefilterMipInitialized = [[], []];
            _prefilterReadTexture = -1;
            _prefilterNextTexture = -1;
            _prefilterBuildTexture = 0;
            _prefilterBuildMip = 0;
            _prefilterBuildActive = false;
            _prefilterBuildSnapshotCaptured = false;
            _prefilterSnapshotUploadRequired = false;
            _buildingSpecularEnvironmentGeneration = 0u;
            _publishedSpecularEnvironmentGeneration = 0u;
            _prefilterTransitionActive = false;
            _prefilterTransitionFrame = 0;
        }

        private EnvironmentPayload CreateHdrEnvironmentPayload(
            ResourceSignature signature,
            uint prefilteredMipCount)
        {
            if (string.IsNullOrWhiteSpace(signature.ResolvedSourcePath))
                throw new ArgumentException(
                    "An HDR equirectangular environment requires SourcePath.",
                    nameof(signature));
            HdrEquirectangularImage hdr =
                EnvironmentMapProcessor.LoadRadianceHdr(signature.ResolvedSourcePath);
            return new EnvironmentPayload(
                EnvironmentMapProcessor.ConvertEquirectangularToCubemap(
                    hdr,
                    signature.EnvironmentSize),
                EnvironmentMapProcessor.GenerateIrradianceCubemap(
                    hdr,
                    signature.IrradianceSize),
                EnvironmentMapProcessor.GeneratePrefilteredEnvironmentCubemap(
                    hdr,
                    signature.PrefilteredSize,
                    prefilteredMipCount));
        }

        private void CreateBakedEnvironmentTextures(
            ResourceSignature signature,
            Format environmentFormat,
            EnvironmentPayload payload)
        {
            _environmentCubemap = _textureManager.CreateCubemap(
                signature.EnvironmentSize,
                environmentFormat,
                mipLevels: 1,
                bindlessIndex: BindlessIndex.EnvironmentCubemapTexture,
                debugName: "Environment Cubemap");
            _textureManager.UploadTextureDataAllMipsAndLayers(
                _environmentCubemap,
                payload.EnvironmentCubemap,
                signature.EnvironmentSize,
                signature.EnvironmentSize,
                environmentFormat);

            _irradianceCubemap = _textureManager.CreateCubemap(
                signature.IrradianceSize,
                environmentFormat,
                mipLevels: 1,
                bindlessIndex: BindlessIndex.IrradianceCubemapTexture,
                debugName: "Diffuse Irradiance Cubemap");
            _textureManager.UploadTextureDataAllMipsAndLayers(
                _irradianceCubemap,
                payload.IrradianceCubemap,
                signature.IrradianceSize,
                signature.IrradianceSize,
                environmentFormat);

            _prefilteredCubemap = _textureManager.CreateCubemap(
                signature.PrefilteredSize,
                environmentFormat,
                mipLevels: _prefilteredMipCount,
                bindlessIndex: BindlessIndex.PrefilteredEnvironmentTexture,
                debugName: "Prefiltered Environment Cubemap");
            _textureManager.UploadTextureDataAllMipsAndLayers(
                _prefilteredCubemap,
                payload.PrefilteredCubemap,
                signature.PrefilteredSize,
                signature.PrefilteredSize,
                environmentFormat);
        }

        private static byte[] GenerateBrdfLut(uint size)
        {
            float[] values = new float[checked((int)(size * size * 4u))];
            int offset = 0;
            for (uint y = 0; y < size; y++)
            {
                float roughness = (y + 0.5f) / size;
                for (uint x = 0; x < size; x++)
                {
                    float nDotV = (x + 0.5f) / size;
                    float scale = 1.0f - 0.5f * roughness * roughness;
                    float bias = 0.04f * (1.0f - nDotV) * (1.0f - roughness);
                    values[offset++] = scale;
                    values[offset++] = bias;
                    values[offset++] = 0.0f;
                    values[offset++] = 1.0f;
                }
            }

            return MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
        }

        private ResourceSignature CreateResourceSignature()
        {
            if (_settings.Environment.SourceKind == EnvironmentSourceKind.Cubemap)
            {
                throw new NotSupportedException(
                    "EnvironmentSourceKind.Cubemap is not implemented. Use an HDR " +
                    "equirectangular source or the procedural atmosphere.");
            }
            string sourcePath = ResolveEnvironmentSourcePath(_settings.Environment.SourcePath) ?? string.Empty;
            return new ResourceSignature(
                _settings.Environment.SourceKind,
                sourcePath,
                _settings.Environment.EnvironmentSize,
                _settings.Environment.IrradianceSize,
                _settings.Environment.PrefilteredSize,
                _settings.Environment.BrdfLutSize,
                _settings.Environment.TexturePrecision);
        }

        private static string? ResolveEnvironmentSourcePath(string? sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
                return null;

            if (Path.IsPathRooted(sourcePath))
                return Path.GetFullPath(sourcePath);

            string currentDirectoryPath = Path.GetFullPath(sourcePath);
            if (File.Exists(currentDirectoryPath))
                return currentDirectoryPath;

            string appDirectoryPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, sourcePath));
            return File.Exists(appDirectoryPath) ? appDirectoryPath : currentDirectoryPath;
        }

        private static uint CalculateMipLevels(uint width, uint height)
        {
            uint levels = 1;
            uint maxDimension = Math.Max(width, height);
            while (maxDimension > 1)
            {
                maxDimension /= 2;
                levels++;
            }

            return levels;
        }

        private static void AddIfValid(
            ICollection<TextureHandle> destination,
            TextureHandle handle)
        {
            if (handle.IsValid)
                destination.Add(handle);
        }

        private static Vector3 ToNumerics(Njulf.Core.Math.Vector3 value) =>
            new(value.X, value.Y, value.Z);

        private static GpuVector4 Pack(Vector3 value, float w) =>
            new(value.X, value.Y, value.Z, w);

        private static GpuVector4 Pack(float[] values, int offset) =>
            new(
                values[offset],
                values[offset + 1],
                values[offset + 2],
                values[offset + 3]);

        private static GpuVector4 PackLast(float[] values, int offset) =>
            new(values[offset], 0.0f, 0.0f, 0.0f);

        private static Format ResolveEnvironmentFormat(EnvironmentTexturePrecision precision)
        {
            return precision == EnvironmentTexturePrecision.Float32
                ? Format.R32G32B32A32Sfloat
                : Format.R16G16B16A16Sfloat;
        }

        private static ulong EstimateCubeBytes(uint size, uint mipLevels, Format format)
        {
            ulong total = 0;
            uint mipSize = size;
            ulong bytesPerPixel = GetBytesPerPixel(format);
            for (uint mip = 0; mip < mipLevels; mip++)
            {
                total = checked(total + (ulong)mipSize * mipSize * 6UL * bytesPerPixel);
                mipSize = Math.Max(1u, mipSize / 2u);
            }

            return total;
        }

        internal static byte[] ConvertRgbaFloat32Payload(ReadOnlySpan<byte> source, Format destinationFormat)
        {
            if (destinationFormat == Format.R32G32B32A32Sfloat)
                return source.ToArray();
            if (destinationFormat != Format.R16G16B16A16Sfloat)
                throw new NotSupportedException($"Environment format {destinationFormat} is not supported.");
            if (source.Length % sizeof(float) != 0)
                throw new ArgumentException("Environment float payload must be aligned to float elements.", nameof(source));

            ReadOnlySpan<float> floats = MemoryMarshal.Cast<byte, float>(source);
            byte[] result = new byte[checked(floats.Length * sizeof(ushort))];
            Span<Half> halves = MemoryMarshal.Cast<byte, Half>(result.AsSpan());
            for (int i = 0; i < floats.Length; i++)
                halves[i] = (Half)floats[i];

            return result;
        }

        private static ulong GetBytesPerPixel(Format format)
        {
            return format switch
            {
                Format.R32G32B32A32Sfloat => 16,
                Format.R16G16B16A16Sfloat => 8,
                _ => throw new NotSupportedException($"Environment format {format} does not have a known byte size.")
            };
        }

        private void DestroyEnvironmentTextures()
        {
            for (int texture = 0; texture < _prefilterStorageViews.Length; texture++)
            {
                ImageView[] views = _prefilterStorageViews[texture];
                for (int mip = 0; mip < views.Length; mip++)
                {
                    if (views[mip].Handle != 0)
                        _textureManager.DestroyTextureSubresourceView(views[mip]);
                }
            }
            ResetPrefilterState();

            if (_environmentCubemap.IsValid)
            {
                _textureManager.DestroyTexture(_environmentCubemap);
                _environmentCubemap = TextureHandle.Invalid;
            }

            if (_irradianceCubemap.IsValid)
            {
                _textureManager.DestroyTexture(_irradianceCubemap);
                _irradianceCubemap = TextureHandle.Invalid;
            }

            if (_prefilteredCubemap.IsValid)
            {
                _textureManager.DestroyTexture(_prefilteredCubemap);
                _prefilteredCubemap = TextureHandle.Invalid;
            }

            if (_nextPrefilteredCubemap.IsValid)
            {
                _textureManager.DestroyTexture(_nextPrefilteredCubemap);
                _nextPrefilteredCubemap = TextureHandle.Invalid;
            }

            if (_brdfLut.IsValid)
            {
                _textureManager.DestroyTexture(_brdfLut);
                _brdfLut = TextureHandle.Invalid;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            DestroyEnvironmentTextures();
            if (_environmentBuffer.IsValid)
                _bufferManager.DestroyBuffer(_environmentBuffer);
            if (_prefilterEnvironmentBuffer.IsValid)
                _bufferManager.DestroyBuffer(_prefilterEnvironmentBuffer);
            if (_giEnvironmentBuffer.IsValid)
                _bufferManager.DestroyBuffer(_giEnvironmentBuffer);
        }

        private readonly record struct EnvironmentPayload(
            byte[] EnvironmentCubemap,
            byte[] IrradianceCubemap,
            byte[] PrefilteredCubemap)
        {
            public EnvironmentPayload ConvertToFormat(Format format)
            {
                return new EnvironmentPayload(
                    ConvertRgbaFloat32Payload(EnvironmentCubemap, format),
                    ConvertRgbaFloat32Payload(IrradianceCubemap, format),
                    ConvertRgbaFloat32Payload(PrefilteredCubemap, format));
            }
        }

        private readonly record struct ResourceSignature(
            EnvironmentSourceKind SourceKind,
            string ResolvedSourcePath,
            uint EnvironmentSize,
            uint IrradianceSize,
            uint PrefilteredSize,
            uint BrdfLutSize,
            EnvironmentTexturePrecision TexturePrecision);
    }

    internal readonly record struct EnvironmentPrefilterWork(
        uint ResourceGeneration,
        int TargetTexture,
        uint MipLevel,
        uint Size,
        float Roughness,
        Image Image,
        ImageView StorageView,
        Format Format,
        ImageLayout OldLayout);
}
