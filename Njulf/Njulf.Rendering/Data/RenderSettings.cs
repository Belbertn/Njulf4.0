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
    /// <summary>Advanced renderer settings and durable configuration. Use ConfigureRendering for startup
    /// and GraphicsDevice.Settings for common runtime changes with application receipts.</summary>
    public sealed class RenderSettings
    {
        /// <summary>Current durable settings-file schema used by capture metadata and persistence.</summary>
        public const int SerializationVersion = 28;
        internal const int MaximumSettingsFileBytes = 4 * 1024 * 1024;

        private float _exposure = 1.0f;
        private float _resolutionScale = 1.0f;

        /// <summary>
        /// Internal pre-publication guard used by renderer-owned tier budgets.
        /// Subscribers run before any preset state mutates and may reject a
        /// transition by throwing, leaving the previous preset intact.
        /// </summary>
        internal event Action<RenderQualityPreset>? QualityPresetChanging;

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
        public OpticalDenoisingSettings OpticalDenoising { get; } = new();
        public AmbientOcclusionSettings AmbientOcclusion { get; } = new();
        public GlobalIlluminationSettings GlobalIllumination { get; } = new();
        public AntiAliasingSettings AntiAliasing { get; } = new();
        public FogSettings Fog { get; } = new();
        public TransparencySettings Transparency { get; } = new();
        public DecalSettings Decals { get; } = new();
        public AnimationSettings Animation { get; } = new();
        public ParticleSettings Particles { get; } = new();
        public FoliageSettings Foliage { get; } = new();
        public SceneSubmissionSettings SceneSubmission { get; } = new();
        public MaterialSettings Materials { get; } = new();
        public RasterSettings Raster { get; } = new();
        public RenderDiagnosticsSettings Diagnostics { get; } = new();
        public HiZVisibilityPolicySettings HiZVisibilityPolicy { get; } = new();
        public HiZOcclusionSettings HiZOcclusion { get; } = new();
        public AsyncComputeSettings AsyncCompute { get; } = new();
        public PerformanceOptimizationSettings PerformanceOptimizations { get; } =
            new();
        public PerformanceOptimizationFeature
            EffectivePerformanceOptimizationFeatures
        {
            get
            {
                PerformanceOptimizationFeature features =
                    PerformanceOptimizations.EffectiveFeatures;
                if (AsyncCompute.Mode == AsyncComputeMode.Disabled)
                {
                    features &= ~PerformanceOptimizationFeature
                        .AsyncGiFarFieldExecution;
                }
                return features;
            }
        }

        public bool IsPerformanceOptimizationEnabled(
            PerformanceOptimizationFeature feature) =>
            feature != PerformanceOptimizationFeature.None &&
            (EffectivePerformanceOptimizationFeatures & feature) == feature;
        public DebugOverlaySettings Debug { get; } = new();
        public RenderBudgetSettings PerformanceBudgets { get; } = new();
        public RenderQualityPreset QualityPreset { get; private set; } = RenderQualityPreset.DdgiHigh;
        public RenderFeatureIsolationMode FeatureIsolation { get; set; } = RenderFeatureIsolationMode.FullFrame;
        public HiZTestMode HiZTestMode { get; set; } = HiZTestMode.Bounds4Tap;
        public bool UseSecondaryCommandBuffers { get; set; } = true;
        public bool UseCameraDependentCpuScenePayload { get; set; } = true;
        public bool UseCpuMeshletFrustumCulling { get; set; } = true;
        /// <summary>
        /// Enables conservative one-sided solid meshlet backface rejection.
        /// Invalid or legacy cone records remain visible and therefore fail
        /// closed without disabling the preset-selected path.
        /// </summary>
        public bool MeshletNormalConeCullingEnabled { get; set; }

        public RenderSettings()
        {
            ApplyQualityPreset(RenderQualityPreset.DdgiHigh);
        }

        /// <summary>
        /// Clears diagnostic visualization overrides without changing the selected
        /// quality profile or its physical rendering settings.
        /// </summary>
        public void ResetRenderViewOverrides()
        {
            ShowRawHdrSceneColor = false;
            FeatureIsolation = RenderFeatureIsolationMode.FullFrame;
            Shadows.DebugView = ShadowDebugView.None;
            Shadows.DirectionalShadowPreviewCascade = 0;
            Shadows.ForceStaticCascadeCacheRefresh = false;
            Bloom.DebugView = BloomDebugView.None;
            Environment.DebugView = EnvironmentDebugView.None;
            Reflections.DebugView = ReflectionDebugView.None;
            AmbientOcclusion.DebugView = AmbientOcclusionDebugView.None;
            GlobalIllumination.DebugView = GlobalIlluminationDebugView.None;
            AntiAliasing.DebugView = AntiAliasingDebugView.None;
            Fog.DebugView = FogDebugView.None;
            Transparency.DebugView = TransparencyDebugView.None;
            Decals.DebugView = DecalDebugView.None;
            Animation.DebugView = AnimationDebugView.None;
            Particles.DebugView = ParticleDebugView.None;
            Foliage.DebugView = FoliageDebugView.None;
            Materials.DebugView = MaterialDebugView.None;
            Debug.Mode = DebugOverlayMode.None;
        }

        public void ApplyQualityPreset(RenderQualityPreset preset)
        {
            if (!Enum.IsDefined(preset))
                throw new ArgumentOutOfRangeException(nameof(preset));

            QualityPresetChanging?.Invoke(preset);
            QualityPreset = preset;
            ShowRawHdrSceneColor = false;

            // Qualification belongs to the renderer profile, never to scene
            // configuration. High-class profiles ship the qualified
            // single-scattering path; only Ultra admits the separately bounded
            // multiple-scattering extension.
            Fog.Volumetric.SingleScatteringQualified = preset is
                RenderQualityPreset.High or
                RenderQualityPreset.DdgiHigh or
                RenderQualityPreset.Ultra;
            Fog.Volumetric.MultipleScatteringQualified =
                preset == RenderQualityPreset.Ultra;
            Fog.Volumetric.MultipleScatteringIterations =
                preset == RenderQualityPreset.Ultra ? 2 : 0;

            // Applying a preset restores the complete production GI request.
            // Runtime and persisted overrides are intentionally applied after
            // this point and can still opt individual features out. Low is the
            // sole no-GI profile and requests none of their resources.
            bool productionGiProfile = preset != RenderQualityPreset.Low;
            GlobalIllumination.DdgiOpacityMicromapMode = productionGiProfile
                ? GlobalIlluminationSettings.DefaultDdgiOpacityMicromapMode
                : DdgiOpacityMicromapMode.Off;
            GlobalIllumination.SimpleDdgiDirectionalGuidingMode =
                productionGiProfile
                    ? GlobalIlluminationSettings
                        .DefaultSimpleDdgiDirectionalGuidingMode
                    : SimpleDdgiDirectionalGuidingMode.Off;
            GlobalIllumination.GiCausticMode = productionGiProfile
                ? GlobalIlluminationSettings.DefaultGiCausticMode
                : GiCausticMode.Off;
            GlobalIllumination.SimpleDdgiReceiverCacheMode =
                productionGiProfile
                    ? GlobalIlluminationSettings
                        .DefaultSimpleDdgiReceiverCacheMode
                    : SimpleDdgiReceiverCacheMode.Exact;
            GlobalIllumination.SimpleDdgiTransportAccelerationEnabled =
                productionGiProfile;
            GlobalIllumination.SimpleDdgiTransportAcceleratedSweepCount = 2;

            // Every non-Low production tier requests the bounded C5 path.
            // Runtime content, memory, and resource completeness remain the
            // only admission gates; an explicit persisted Off is applied
            // after the preset and still wins.
            GlobalIllumination.SimpleDdgiNearFieldResidualMode =
                productionGiProfile
                    ? GlobalIlluminationSettings
                        .DefaultSimpleDdgiNearFieldResidualMode
                    : SimpleDdgiNearFieldResidualMode.Off;
            GlobalIllumination.SimpleDdgiNearFieldResidualQualityPreset =
                preset switch
                {
                    RenderQualityPreset.Medium =>
                        SimpleDdgiNearFieldResidualQualityPreset.Performance,
                    RenderQualityPreset.Ultra =>
                        SimpleDdgiNearFieldResidualQualityPreset.Quality,
                    _ => SimpleDdgiNearFieldResidualQualityPreset.Balanced
                };

            // High, DdgiHigh, and Ultra retain their authored bent-normal quality. The
            // receiver-cache variants consume the same normal-dependent
            // environment and compact-directional inputs in the forward path.
            AmbientOcclusion.Mode = preset == RenderQualityPreset.Low
                ? AmbientOcclusionMode.Disabled
                : AmbientOcclusionMode.Gtao;
            AmbientOcclusion.BentNormalMode = preset switch
            {
                RenderQualityPreset.High =>
                    AmbientOcclusionBentNormalMode.EnvironmentOnly,
                RenderQualityPreset.DdgiHigh =>
                    AmbientOcclusionBentNormalMode.EnvironmentAndDdgi,
                RenderQualityPreset.Ultra =>
                    AmbientOcclusionBentNormalMode.EnvironmentAndDdgi,
                _ => AmbientOcclusionBentNormalMode.Off
            };
            GlobalIllumination
                .SimpleDdgiNearFieldResidualLocalAdaptiveSchedulingEnabled =
                productionGiProfile;
            Shadows.ApplyRenderQualityPreset(preset);
            AsyncCompute.PreferredPathMask = productionGiProfile
                ? AsyncComputeSettings.DefaultPreferredPathMask
                : AsyncComputePreferredPathMask.None;
            SceneSubmission.EnableProductionMeshletFeatures();
            Foliage.IndirectMeshletDispatchEnabled = true;
            MeshletNormalConeCullingEnabled = true;
            Transparency.PipelinePartitioningEnabled = true;
            SceneSubmission.GpuLodTargetPixelError = preset switch
            {
                RenderQualityPreset.Low => 2.0f,
                RenderQualityPreset.Medium => 1.5f,
                RenderQualityPreset.Ultra => 0.5f,
                _ => 1.0f
            };
            Reflections.CaptureLodEnabled = true;
            Reflections.CaptureLodTargetPixelError = ReflectionSettings.CaptureLodErrorFor(preset);
            Materials.SpecularAntialiasingMode = preset ==
                RenderQualityPreset.Low
                    ? SpecularAntialiasingMode.Off
                    : SpecularAntialiasingMode.GeometricVariance;
            Raster.VariableRateShadingMode = preset is
                RenderQualityPreset.Low or
                RenderQualityPreset.Medium or
                RenderQualityPreset.DdgiHigh
                ? VariableRateShadingMode.Auto
                : VariableRateShadingMode.Off;
            Raster.MeshShaderTuningMode = MeshShaderTuningMode.Auto;

            AmbientOcclusion.GtaoQualityPreset = preset switch
            {
                RenderQualityPreset.Low => GtaoQualityPreset.Low,
                RenderQualityPreset.Medium => GtaoQualityPreset.Low,
                RenderQualityPreset.High => GtaoQualityPreset.Balanced,
                RenderQualityPreset.DdgiHigh => GtaoQualityPreset.High,
                RenderQualityPreset.Ultra => GtaoQualityPreset.High,
                _ => GtaoQualityPreset.Balanced
            };

            switch (preset)
            {
                case RenderQualityPreset.Low:
                    ResolutionScale = 0.75f;
                    DynamicResolution.Enabled = false;
                    DynamicResolution.MinimumScale = 0.5f;
                    DynamicResolution.MaximumScale = 0.85f;
                    Bloom.Enabled = false;
                    Fog.Enabled = false;
                    AmbientOcclusion.Enabled = false;
                    GlobalIllumination.Enabled = false;
                    GlobalIllumination.Mode = GlobalIlluminationMode.Disabled;
                    GlobalIllumination.DebugView = GlobalIlluminationDebugView.None;
                    GlobalIllumination.IndirectIntensity = 0.0f;
                    GlobalIllumination.EnvironmentFallbackIntensity = 1.0f;
                    GlobalIllumination.UseDdgi = false;
                    GlobalIllumination.UseRayQueryBackend = false;
                    GlobalIllumination.ApplyDdgiQualityTier(DdgiQualityTier.DdgiLow);
                    GlobalIllumination.DdgiProbeClassificationEnabled = true;
                    GlobalIllumination.DdgiProbeRelocationEnabled = false;
                    GlobalIllumination.DdgiCameraRelativeEnabled = false;
                    GlobalIllumination.DdgiAsyncComputeEnabled = false;
                    GlobalIllumination.DdgiProbeUpdatePrimaryRayBudget = GlobalIlluminationSettings.DefaultDdgiProbeUpdatePrimaryRayBudget;
                    GlobalIllumination.ResolutionScale = 0.5f;
                    GlobalIllumination.MaxBounceDistance = 3.0f;
                    Reflections.Enabled = true;
                    Reflections.Mode = ReflectionMode.StaticProbes;
                    Particles.Enabled = true;
                    Foliage.Enabled = true;
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
                    Transparency.Mode = TransparencyMode.SortedAlphaBlend;
                    Transparency.ReceiveGlobalIllumination = false;
                    Transparency.SampleReflections = false;
                    Transparency.SceneReflectionRayTaskBudget = 0;
                    Transparency.SceneReflectionSsrSampleBudget = 0;
                    Decals.ReceiveGlobalIllumination = false;
                    break;
                case RenderQualityPreset.Medium:
                    ResolutionScale = 0.9f;
                    DynamicResolution.Enabled = false;
                    DynamicResolution.MinimumScale = 0.65f;
                    DynamicResolution.MaximumScale = 1.0f;
                    Bloom.Enabled = true;
                    Bloom.MipCount = 5;
                    Fog.Enabled = false;
                    AmbientOcclusion.Enabled = true;
                    AmbientOcclusion.ResolutionScale = 0.5f;
                    AmbientOcclusion.SampleCount = 8;
                    GlobalIllumination.Enabled = true;
                    GlobalIllumination.Mode = GlobalIlluminationMode.Ddgi;
                    GlobalIllumination.DebugView = GlobalIlluminationDebugView.None;
                    GlobalIllumination.IndirectIntensity = 0.75f;
                    GlobalIllumination.EnvironmentFallbackIntensity = 1.0f;
                    GlobalIllumination.UseDdgi = true;
                    GlobalIllumination.UseRayQueryBackend = true;
                    GlobalIllumination.ApplyDdgiQualityTier(DdgiQualityTier.DdgiMedium);
                    GlobalIllumination.DdgiProbeClassificationEnabled = true;
                    GlobalIllumination.DdgiProbeRelocationEnabled = false;
                    GlobalIllumination.DdgiCameraRelativeEnabled = false;
                    GlobalIllumination.DdgiAsyncComputeEnabled = false;
                    GlobalIllumination.DdgiProbeUpdatePrimaryRayBudget = GlobalIlluminationSettings.DefaultDdgiProbeUpdatePrimaryRayBudget;
                    GlobalIllumination.ResolutionScale = 0.5f;
                    GlobalIllumination.MaxBounceDistance = 4.0f;
                    GlobalIllumination.TemporalEnabled = true;
                    GlobalIllumination.DenoiserEnabled = true;
                    Reflections.Enabled = true;
                    Reflections.MaxProbesPerPixel = 1;
                    Reflections.ApplyHybridQualityBudget(RenderQualityPreset.Medium);
                    Particles.Enabled = true;
                    Foliage.Enabled = true;
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
                    Transparency.Mode = TransparencyMode.SortedAlphaBlend;
                    // Dynamic diffuse GI is intentionally not sampled by
                    // layered forward receivers in this quality tier.
                    Transparency.ReceiveGlobalIllumination = false;
                    Transparency.SampleReflections = false;
                    Transparency.SceneReflectionRayTaskBudget = 0;
                    Transparency.SceneReflectionSsrSampleBudget = 0;
                    Decals.ReceiveGlobalIllumination = false;
                    break;
                case RenderQualityPreset.DdgiHigh:
                    ResolutionScale = 1.0f;
                    DynamicResolution.Enabled = false;
                    Bloom.Enabled = true;
                    Bloom.MipCount = 6;
                    Fog.Enabled = false;
                    AmbientOcclusion.Enabled = true;
                    AmbientOcclusion.ResolutionScale = 1.0f;
                    AmbientOcclusion.SampleCount = 32;
                    GlobalIllumination.Enabled = true;
                    GlobalIllumination.Mode = GlobalIlluminationMode.Ddgi;
                    GlobalIllumination.DebugView = GlobalIlluminationDebugView.None;
                    GlobalIllumination.IndirectIntensity = 1.0f;
                    GlobalIllumination.EnvironmentFallbackIntensity = 1.0f;
                    GlobalIllumination.UseDdgi = true;
                    GlobalIllumination.UseRayQueryBackend = true;
                    GlobalIllumination.DdgiProbeClassificationEnabled = true;
                    GlobalIllumination.DdgiProbeRelocationEnabled = true;
                    GlobalIllumination.DdgiCameraRelativeEnabled = true;
                    GlobalIllumination.ApplyDdgiQualityTier(DdgiQualityTier.DdgiHigh);
                    GlobalIllumination.DdgiAsyncComputeEnabled = true;
                    GlobalIllumination.ResolutionScale = 0.5f;
                    GlobalIllumination.MaxBounceDistance = 10.0f;
                    GlobalIllumination.TemporalEnabled = false;
                    GlobalIllumination.DenoiserEnabled = false;
                    Reflections.Enabled = true;
                    Reflections.MaxProbesPerPixel = 2;
                    Reflections.ApplyHybridQualityBudget(RenderQualityPreset.DdgiHigh);
                    Particles.Enabled = true;
                    Foliage.Enabled = true;
                    Foliage.HiZCullingEnabled = true;
                    Foliage.CastShadows = true;
                    Foliage.DensityScale = 1.0f;
                    Foliage.MaxDrawDistance = 250f;
                    Foliage.GrassShadowDistance = 25f;
                    Foliage.GrassShadowDensityScale = 0.5f;
                    Foliage.LocalShadowsEnabled = true;
                    Foliage.MaxLocalShadowedSpotLights = 1;
                    Foliage.MaxLocalShadowedPointLights = 1;
                    Foliage.MaxLocalShadowClusters = 4096;
                    Foliage.MaxLocalShadowMeshletDraws = 8192;
                    Foliage.MaxVisibleClusters = 262144;
                    Foliage.MaxVisibleMeshletDraws = 524288;
                    AntiAliasing.Mode = AntiAliasingMode.SmaaHigh;
                    Transparency.Mode = TransparencyMode.SortedAlphaBlend;
                    Transparency.ReceiveGlobalIllumination = true;
                    Transparency.SampleReflections = true;
                    Transparency.SceneReflectionRayTaskBudget = 65_536;
                    Transparency.SceneReflectionSsrSampleBudget = 4_194_304;
                    Decals.ReceiveGlobalIllumination = true;
                    break;
                case RenderQualityPreset.Ultra:
                    ResolutionScale = 1.0f;
                    DynamicResolution.Enabled = false;
                    Bloom.Enabled = true;
                    Bloom.MipCount = 8;
                    Fog.Enabled = false;
                    AmbientOcclusion.Enabled = true;
                    AmbientOcclusion.ResolutionScale = 1.0f;
                    AmbientOcclusion.SampleCount = 32;
                    GlobalIllumination.Enabled = true;
                    GlobalIllumination.Mode = GlobalIlluminationMode.Ddgi;
                    GlobalIllumination.DebugView = GlobalIlluminationDebugView.None;
                    GlobalIllumination.IndirectIntensity = 1.0f;
                    GlobalIllumination.EnvironmentFallbackIntensity = 1.0f;
                    GlobalIllumination.UseDdgi = true;
                    GlobalIllumination.UseRayQueryBackend = true;
                    GlobalIllumination.DdgiProbeClassificationEnabled = true;
                    GlobalIllumination.DdgiProbeRelocationEnabled = true;
                    GlobalIllumination.DdgiCameraRelativeEnabled = true;
                    GlobalIllumination.DdgiAsyncComputeEnabled = true;
                    GlobalIllumination.ApplyDdgiQualityTier(DdgiQualityTier.DdgiUltra);
                    GlobalIllumination.ResolutionScale = 0.5f;
                    GlobalIllumination.MaxBounceDistance = 10.0f;
                    GlobalIllumination.TemporalEnabled = true;
                    GlobalIllumination.DenoiserEnabled = true;
                    Reflections.Enabled = true;
                    Reflections.MaxProbesPerPixel = ReflectionSettings.ShaderMaxProbesPerPixel;
                    Reflections.ApplyHybridQualityBudget(RenderQualityPreset.Ultra);
                    Particles.Enabled = true;
                    Foliage.Enabled = true;
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
                    AntiAliasing.Mode = AntiAliasingMode.SmaaUltra;
                    Transparency.Mode = TransparencyMode.SortedAlphaBlend;
                    Transparency.ReceiveGlobalIllumination = true;
                    Transparency.SampleReflections = true;
                    Transparency.SceneReflectionRayTaskBudget = 131_072;
                    Transparency.SceneReflectionSsrSampleBudget = 8_388_608;
                    Decals.ReceiveGlobalIllumination = true;
                    break;
                case RenderQualityPreset.High:
                    ResolutionScale = 1.0f;
                    DynamicResolution.Enabled = false;
                    Bloom.Enabled = true;
                    Bloom.MipCount = 6;
                    Fog.Enabled = false;
                    AmbientOcclusion.Enabled = true;
                    AmbientOcclusion.ResolutionScale = 0.5f;
                    AmbientOcclusion.SampleCount = 16;
                    GlobalIllumination.Enabled = true;
                    GlobalIllumination.Mode = GlobalIlluminationMode.Ddgi;
                    GlobalIllumination.DebugView = GlobalIlluminationDebugView.None;
                    GlobalIllumination.IndirectIntensity = 1.0f;
                    GlobalIllumination.EnvironmentFallbackIntensity = 1.0f;
                    GlobalIllumination.UseDdgi = true;
                    // High enables the production DDGI path. Leaving its only
                    // shipping trace backend disabled produces an internally
                    // contradictory profile: the gather and SSGI residual run,
                    // but no probe can ever acquire irradiance or visibility.
                    GlobalIllumination.UseRayQueryBackend = true;
                    GlobalIllumination.ApplyDdgiQualityTier(DdgiQualityTier.DdgiHigh);
                    GlobalIllumination.DdgiProbeClassificationEnabled = true;
                    GlobalIllumination.DdgiProbeRelocationEnabled = true;
                    GlobalIllumination.DdgiCameraRelativeEnabled = true;
                    GlobalIllumination.DdgiAsyncComputeEnabled = true;
                    GlobalIllumination.DdgiProbeUpdatePrimaryRayBudget = GlobalIlluminationSettings.DefaultDdgiProbeUpdatePrimaryRayBudget;
                    GlobalIllumination.ResolutionScale = 0.5f;
                    GlobalIllumination.MaxBounceDistance = 6.0f;
                    GlobalIllumination.TemporalEnabled = true;
                    GlobalIllumination.DenoiserEnabled = true;
                    Reflections.Enabled = true;
                    Reflections.MaxProbesPerPixel = 2;
                    Reflections.ApplyHybridQualityBudget(RenderQualityPreset.High);
                    Particles.Enabled = true;
                    Foliage.Enabled = true;
                    Foliage.HiZCullingEnabled = true;
                    Foliage.CastShadows = true;
                    Foliage.DensityScale = 1.0f;
                    Foliage.MaxDrawDistance = 250f;
                    Foliage.GrassShadowDistance = 25f;
                    Foliage.GrassShadowDensityScale = 0.5f;
                    Foliage.LocalShadowsEnabled = true;
                    Foliage.MaxLocalShadowedSpotLights = 1;
                    Foliage.MaxLocalShadowedPointLights = 1;
                    Foliage.MaxLocalShadowClusters = 4096;
                    Foliage.MaxLocalShadowMeshletDraws = 8192;
                    Foliage.MaxVisibleClusters = 262144;
                    Foliage.MaxVisibleMeshletDraws = 524288;
                    AntiAliasing.Mode = AntiAliasingMode.SmaaMedium;
                    Transparency.Mode = TransparencyMode.SortedAlphaBlend;
                    Transparency.ReceiveGlobalIllumination = true;
                    Transparency.SampleReflections = true;
                    Transparency.SceneReflectionRayTaskBudget = 65_536;
                    Transparency.SceneReflectionSsrSampleBudget = 4_194_304;
                    Decals.ReceiveGlobalIllumination = true;
                    break;
            }
        }

        public void Save(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Render settings path cannot be null or empty.", nameof(path));

            byte[] payload = SerializePersistencePayload();

            string fullPath = Path.GetFullPath(path);
            string directory =
                Path.GetDirectoryName(fullPath)
                ?? throw new InvalidOperationException(
                    $"Render settings path '{fullPath}' has no parent directory.");
            Directory.CreateDirectory(directory);
            string temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var output = new FileStream(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           bufferSize: 64 * 1024,
                           options: FileOptions.WriteThrough))
                {
                    output.Write(payload);
                    output.Flush(flushToDisk: true);
                }

                if (File.Exists(fullPath))
                {
                    File.Replace(
                        temporaryPath,
                        fullPath,
                        destinationBackupFileName: null,
                        ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporaryPath, fullPath);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }

        /// <summary>
        /// Creates a detached, persistence-equivalent settings snapshot.
        /// Renderer-owned callbacks and mutable child objects are deliberately
        /// not shared with the source instance, making the result safe to edit
        /// as a cold-start transaction while the live renderer keeps running.
        /// </summary>
        public RenderSettings CreateSnapshot()
        {
            var snapshot = new RenderSettings();
            RenderSettingsFile.FromSettings(this).ApplyTo(snapshot);
            return snapshot;
        }

        /// <summary>
        /// Hashes the complete deterministic persisted document, including
        /// non-GI settings. Advanced-GI qualification uses its narrower GI
        /// fingerprint, while startup transaction integrity uses this hash so
        /// an older profile cannot observe a different settings snapshot.
        /// </summary>
        public string ComputePersistenceSha256()
        {
            byte[] payload = SerializePersistencePayload();
            return "sha256:" + Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(payload))
                .ToLowerInvariant();
        }

        public static RenderSettings Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Render settings path cannot be null or empty.", nameof(path));
            string fullPath = Path.GetFullPath(path);
            using var input = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                options: FileOptions.SequentialScan);
            long admittedLength = input.Length;
            if (admittedLength <= 0 ||
                admittedLength > MaximumSettingsFileBytes)
            {
                throw new InvalidDataException(
                    $"Render settings file '{fullPath}' contains {admittedLength} bytes; " +
                    $"the valid range is [1, {MaximumSettingsFileBytes}].");
            }

            var payload = new byte[checked((int)admittedLength)];
            try
            {
                input.ReadExactly(payload);
            }
            catch (EndOfStreamException exception)
            {
                throw new InvalidDataException(
                    $"Render settings file '{fullPath}' became shorter during its bounded read.",
                    exception);
            }

            if (input.ReadByte() != -1 ||
                input.Length != admittedLength)
            {
                throw new InvalidDataException(
                    $"Render settings file '{fullPath}' changed length during its bounded read.");
            }

            RenderSettingsFile? file =
                JsonSerializer.Deserialize<RenderSettingsFile>(
                    payload,
                    CreateJsonOptions());
            if (file == null)
            {
                throw new InvalidDataException(
                    $"Render settings file '{fullPath}' did not contain a valid settings object.");
            }

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

        private byte[] SerializePersistencePayload()
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
                RenderSettingsFile.FromSettings(this),
                CreateJsonOptions());
            if (payload.Length > MaximumSettingsFileBytes)
            {
                throw new InvalidOperationException(
                    $"Render settings output contains {payload.Length} bytes, exceeding " +
                    $"the {MaximumSettingsFileBytes}-byte limit.");
            }
            return payload;
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
            // A missing version is legacy by definition. Version 4 adds explicit
            // XYZ serialization for authored Simple-DDGI volumes; Vector3 exposes
            // fields rather than properties and therefore cannot safely be left to
            // the default System.Text.Json contract. Version 5 makes the four
            // material-GI V2 rollout switches fail-closed; release qualification
            // remains an external, non-persisted policy. Version 6 persists the
            // independently configurable transparent/decal DDGI receiver policy.
            // Version 7 persists the procedural-atmosphere authoring contract,
            // including explicit XYZ values for its vector fields. Version 8
            // persists independent decal shadow reception; material isolation
            // remains an invocation-scoped benchmark override. Version 9
            // replaces DDGI's overloaded light cap and rough-specular boolean
            // with typed content-dependent modes and independent budgets.
            // Version 10 adds requested advanced-GI modes plus evidence IDs;
            // the previous experiment booleans remain read-compatible aliases.
            // Version 11 replaces the directional ShadowsEnabled scalar with a
            // complete nested directional-shadow production contract. Version
            // 13 adds finite-sun diameter scaling and adaptive per-cascade PCF;
            // older nested shadow objects default to scale 1 and constant radius.
            // Version 14 adds C5 presets and bounded advanced overrides and
            // invalidates every older C5 qualification ID. Version 15 makes
            // explicit adaptive C5 the default and upgrades older
            // AutoQualified requests while preserving explicit opt-outs.
            // Version 16 persists the froxel volumetric-fog contract. Version 17
            // persists the hybrid-reflection mode, quality budgets, and filters.
            // Version 18 persists bounded thick-transmission, nested-media,
            // water-boundary, and optional RGB-dispersion budgets. Version 19
            // persists the bounded transparent scene-reflection ray budget.
            // Version 20 independently bounds transparent SSR Hi-Z samples;
            // missing values inherit the selected preset's safe budget.
            // Version 21 persists the distinct GTAO preset and its safe,
            // default-off bent-normal lighting gate. Version 22 promotes the
            // preset-owned GTAO/bent-normal, receiver-cache, C5 local scheduling,
            // meshlet cone, and transparency partition defaults while retaining
            // explicit current-schema overrides. Version 23 persists automatic,
            // resolution-aware cooked LOD selection and its pixel-error budget;
            // older files now promote that v2-only production behavior. Version
            // 24 persists eight-frame LOD dithering, hierarchy traversal, and
            // the bounded authenticated meshlet-streaming budgets.
            // Version 25 persists the taskless mesh-shader tuning contract and
            // the remaining renderer activation controls introduced together.
            // Version 26 persists adaptive-reflection implementation selection
            // and its compact receiver/history contracts after the combined
            // renderer plans consumed the in-progress version-25 schema.
            // Version 27 persists the quality-locked performance campaign's
            // master switch and independently reversible feature mask.
            public int? Version { get; init; }
            public RenderQualityPreset QualityPreset { get; init; } = RenderQualityPreset.DdgiHigh;
            public float ResolutionScale { get; init; } = 1.0f;
            public DynamicResolutionFile DynamicResolution { get; init; } = new();
            public ToneMapper ToneMapper { get; init; } = ToneMapper.AcesFitted;
            public float Exposure { get; init; } = 1.0f;
            public bool AutoExposureEnabled { get; init; } = true;
            public AntiAliasingMode AntiAliasingMode { get; init; } = AntiAliasingMode.SmaaMedium;
            public bool BloomEnabled { get; init; } = true;
            public bool AmbientOcclusionEnabled { get; init; } = true;
            public AmbientOcclusionFile? AmbientOcclusion { get; init; }
            public EnvironmentFile? Environment { get; init; }
            public GlobalIlluminationFile? GlobalIllumination { get; init; }
            public bool FogEnabled { get; init; }
            public FogFile? Fog { get; init; }
            public bool ReflectionsEnabled { get; init; } = true;
            public ReflectionSettingsFile? Reflections { get; init; }
            public OpticalDenoisingSettings? OpticalDenoising { get; init; }
            public ShadowSettingsFile? Shadows { get; init; }
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public bool? ShadowsEnabled { get; init; }
            public bool ParticlesEnabled { get; init; } = true;
            public bool? MeshletNormalConeCullingEnabled { get; init; }
            public SpecularAntialiasingMode? SpecularAntialiasingMode { get; init; }
            public VariableRateShadingMode? VariableRateShadingMode { get; init; }
            public MeshShaderTuningMode? MeshShaderTuningMode { get; init; }
            public TransparencySettingsFile? Transparency { get; init; }
            public bool? TransparentReceiveGlobalIllumination { get; init; }
            public bool? DecalReceiveGlobalIllumination { get; init; }
            public bool? DecalReceiveShadows { get; init; }
            public FoliageFile Foliage { get; init; } = new();
            public SceneSubmissionFile SceneSubmission { get; init; } = new();
            public HiZOcclusionFile HiZOcclusion { get; init; } = new();
            public AsyncComputeFile AsyncCompute { get; init; } = new();
            public PerformanceOptimizationsFile? PerformanceOptimizations
            {
                get;
                init;
            }
            public bool GpuMeshletCountersEnabled { get; init; }
            public bool DdgiForwardEstimateCountersEnabled { get; init; }

            public static RenderSettingsFile FromSettings(RenderSettings settings)
            {
                return new RenderSettingsFile
                {
                    Version = SerializationVersion,
                    QualityPreset = settings.QualityPreset,
                    ResolutionScale = settings.ResolutionScale,
                    DynamicResolution = DynamicResolutionFile.FromSettings(settings.DynamicResolution),
                    ToneMapper = settings.ToneMapper,
                    Exposure = settings.Exposure,
                    AutoExposureEnabled = settings.AutoExposure.Enabled,
                    AntiAliasingMode = settings.AntiAliasing.Mode,
                    BloomEnabled = settings.Bloom.Enabled,
                    AmbientOcclusionEnabled = settings.AmbientOcclusion.Enabled,
                    AmbientOcclusion = AmbientOcclusionFile.FromSettings(
                        settings.AmbientOcclusion),
                    Environment = EnvironmentFile.FromSettings(settings.Environment),
                    GlobalIllumination = GlobalIlluminationFile.FromSettings(settings.GlobalIllumination),
                    FogEnabled = settings.Fog.Enabled,
                    Fog = FogFile.FromSettings(settings.Fog),
                    ReflectionsEnabled = settings.Reflections.Enabled,
                    Reflections = ReflectionSettingsFile.FromSettings(settings.Reflections),
                    OpticalDenoising = new OpticalDenoisingSettings
                    {
                        Enabled = settings.OpticalDenoising.Enabled,
                        BypassFilter = settings.OpticalDenoising.BypassFilter,
                        CompactLayers = settings.OpticalDenoising.CompactLayers,
                        ReducedResolutionShading = settings.OpticalDenoising.ReducedResolutionShading,
                        LayerLimit = settings.OpticalDenoising.LayerLimit,
                        MemoryBudgetMiB = settings.OpticalDenoising.MemoryBudgetMiB
                    },
                    Shadows = ShadowSettingsFile.FromSettings(settings.Shadows),
                    ParticlesEnabled = settings.Particles.Enabled,
                    MeshletNormalConeCullingEnabled =
                        settings.MeshletNormalConeCullingEnabled,
                    SpecularAntialiasingMode =
                        settings.Materials.SpecularAntialiasingMode,
                    VariableRateShadingMode =
                        settings.Raster.VariableRateShadingMode,
                    MeshShaderTuningMode =
                        settings.Raster.MeshShaderTuningMode,
                    Transparency = TransparencySettingsFile.FromSettings(
                        settings.Transparency),
                    TransparentReceiveGlobalIllumination =
                        settings.Transparency.ReceiveGlobalIllumination,
                    DecalReceiveGlobalIllumination =
                        settings.Decals.ReceiveGlobalIllumination,
                    DecalReceiveShadows = settings.Decals.ReceiveShadows,
                    Foliage = FoliageFile.FromSettings(settings.Foliage),
                    SceneSubmission = SceneSubmissionFile.FromSettings(settings.SceneSubmission),
                    HiZOcclusion = HiZOcclusionFile.FromSettings(settings.HiZOcclusion),
                    AsyncCompute = AsyncComputeFile.FromSettings(settings.AsyncCompute),
                    PerformanceOptimizations =
                        PerformanceOptimizationsFile.FromSettings(
                            settings.PerformanceOptimizations),
                    GpuMeshletCountersEnabled = settings.Diagnostics.GpuMeshletCountersEnabled,
                    DdgiForwardEstimateCountersEnabled = settings.Diagnostics.DdgiForwardEstimateCountersEnabled
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
                if (Version.GetValueOrDefault() >= 21 &&
                    AmbientOcclusion != null)
                {
                    AmbientOcclusion.ApplyTo(
                        settings.AmbientOcclusion,
                        Version.GetValueOrDefault());
                }
                else
                {
                    settings.AmbientOcclusion.Enabled =
                        AmbientOcclusionEnabled;
                }
                if (OpticalDenoising is { } optical)
                {
                    settings.OpticalDenoising.Enabled = optical.Enabled;
                    settings.OpticalDenoising.BypassFilter = optical.BypassFilter;
                    settings.OpticalDenoising.CompactLayers = optical.CompactLayers;
                    settings.OpticalDenoising.ReducedResolutionShading = optical.ReducedResolutionShading;
                    settings.OpticalDenoising.LayerLimit = optical.LayerLimit;
                    settings.OpticalDenoising.MemoryBudgetMiB = optical.MemoryBudgetMiB;
                }
                Environment?.ApplyTo(settings.Environment);
                GlobalIllumination?.ApplyTo(
                    settings.GlobalIllumination,
                    Version.GetValueOrDefault());
                // Rollout authority is never persisted. This also clears V2
                // booleans from current-version files so no raw consumer can
                // observe an unauthenticated feature request during startup.
                settings.GlobalIllumination.UseLegacyMaterialGiRollout();
                settings.GlobalIllumination.UseQualifiedContentDependentBaseline();
                if (Fog != null)
                    Fog.ApplyTo(settings.Fog);
                else
                    settings.Fog.Enabled = FogEnabled;
                if (Version.GetValueOrDefault() >= 17 && Reflections != null)
                {
                    Reflections.ApplyTo(settings.Reflections);
                }
                else
                {
                    // Legacy files persisted only the master switch. Preserve the
                    // selected preset's reflection mode and apply the old opt-in.
                    settings.Reflections.Enabled = ReflectionsEnabled;
                }
                if (Shadows != null)
                {
                    Shadows.ApplyTo(
                        settings.Shadows,
                        Version.GetValueOrDefault());
                }
                else if (ShadowsEnabled.HasValue)
                {
                    // Version 10 and older persisted only the enable switch. All
                    // other directional controls retain preset/default values and
                    // legacy rendering behavior.
                    settings.Shadows.DirectionalShadowsEnabled = ShadowsEnabled.Value;
                    settings.Shadows.RequestedDirectionalShadowMode =
                        DirectionalShadowMode.Cascaded;
                    settings.Shadows.DirectionalFilterMode =
                        DirectionalShadowFilterMode.LegacyBoxPcf;
                    settings.Shadows.DirectionalBiasMode =
                        DirectionalShadowBiasMode.Legacy;
                    settings.Shadows.DirectionalPcfRadiusMode =
                        DirectionalPcfRadiusMode.Constant;
                    settings.Shadows.DirectionalSoftAngularDiameterScale = 1f;
                }
                settings.Particles.Enabled = ParticlesEnabled;
                if (Version.GetValueOrDefault() >= 22 &&
                    MeshletNormalConeCullingEnabled.HasValue)
                {
                    settings.MeshletNormalConeCullingEnabled =
                        MeshletNormalConeCullingEnabled.Value;
                }
                settings.Materials.SpecularAntialiasingMode =
                    Version.GetValueOrDefault() >= 23
                        ? SpecularAntialiasingMode ??
                            global::Njulf.Rendering.Data.SpecularAntialiasingMode.GeometricVariance
                        : global::Njulf.Rendering.Data.SpecularAntialiasingMode.Off;
                if (Version.GetValueOrDefault() >= 23 &&
                    VariableRateShadingMode.HasValue)
                {
                    settings.Raster.VariableRateShadingMode =
                        VariableRateShadingMode.Value;
                }
                if (Version.GetValueOrDefault() >= 25 &&
                    MeshShaderTuningMode.HasValue)
                {
                    settings.Raster.MeshShaderTuningMode =
                        MeshShaderTuningMode.Value;
                }
                if (Version.GetValueOrDefault() >= 18 && Transparency != null)
                {
                    Transparency.ApplyTo(
                        settings.Transparency,
                        Version.GetValueOrDefault());
                }
                else if (TransparentReceiveGlobalIllumination.HasValue)
                {
                    settings.Transparency.ReceiveGlobalIllumination =
                        TransparentReceiveGlobalIllumination.Value;
                }
                if (DecalReceiveGlobalIllumination.HasValue)
                {
                    settings.Decals.ReceiveGlobalIllumination =
                        DecalReceiveGlobalIllumination.Value;
                }
                if (DecalReceiveShadows.HasValue)
                    settings.Decals.ReceiveShadows = DecalReceiveShadows.Value;
                Foliage.ApplyTo(settings.Foliage);
                SceneSubmission.ApplyTo(
                    settings.SceneSubmission,
                    Version.GetValueOrDefault());
                HiZOcclusion.ApplyTo(settings.HiZOcclusion);
                // Version 3 introduced Auto as the fresh-install default. Older files did not
                // have a trustworthy policy field, so retain their graphics-only behavior when
                // Mode is absent instead of silently changing an existing installation.
                AsyncCompute.ApplyTo(settings.AsyncCompute, missingModeMeansDisabled: !Version.HasValue || Version.Value < 3);
                PerformanceOptimizations?.ApplyTo(
                    settings.PerformanceOptimizations);
                settings.Diagnostics.GpuMeshletCountersEnabled = GpuMeshletCountersEnabled;
                settings.Diagnostics.DdgiForwardEstimateCountersEnabled = DdgiForwardEstimateCountersEnabled;
            }
        }

        private sealed record PerformanceOptimizationsFile
        {
            public bool Enabled { get; init; } = true;
            public PerformanceOptimizationFeature EnabledFeatures { get; init; } =
                PerformanceOptimizationFeature.All;

            public static PerformanceOptimizationsFile FromSettings(
                PerformanceOptimizationSettings settings) => new()
            {
                Enabled = settings.Enabled,
                EnabledFeatures = settings.EnabledFeatures &
                    PerformanceOptimizationFeature.All
            };

            public void ApplyTo(PerformanceOptimizationSettings settings)
            {
                settings.Enabled = Enabled;
                settings.EnabledFeatures = EnabledFeatures &
                    PerformanceOptimizationFeature.All;
            }
        }

        private sealed record AmbientOcclusionFile
        {
            public bool Enabled { get; init; } = true;
            public AmbientOcclusionMode Mode { get; init; } =
                AmbientOcclusionMode.Ssao;
            public float ResolutionScale { get; init; } = 0.5f;
            public float Radius { get; init; } = 0.75f;
            public float Intensity { get; init; } = 1.0f;
            public float Bias { get; init; } = 0.03f;
            public float Power { get; init; } = 1.2f;
            public int SampleCount { get; init; } = 16;
            public int BlurRadius { get; init; } = 2;
            public float DepthSigma { get; init; } = 2.0f;
            public float NormalSigma { get; init; } = 32.0f;
            public bool UseSceneNormals { get; init; }
            public AmbientOcclusionDebugView DebugView { get; init; }
            public GtaoQualityPreset GtaoQualityPreset { get; init; } =
                GtaoQualityPreset.Balanced;
            public float GtaoThickness { get; init; } = 0.15f;
            public float GtaoFalloff { get; init; } = 1.0f;
            public AmbientOcclusionBentNormalMode BentNormalMode { get; init; }

            public static AmbientOcclusionFile FromSettings(
                AmbientOcclusionSettings settings) => new()
            {
                Enabled = settings.Enabled,
                Mode = settings.Mode,
                ResolutionScale = settings.ResolutionScale,
                Radius = settings.Radius,
                Intensity = settings.Intensity,
                Bias = settings.Bias,
                Power = settings.Power,
                SampleCount = settings.SampleCount,
                BlurRadius = settings.BlurRadius,
                DepthSigma = settings.DepthSigma,
                NormalSigma = settings.NormalSigma,
                UseSceneNormals = settings.UseSceneNormals,
                DebugView = settings.DebugView,
                GtaoQualityPreset = settings.GtaoQualityPreset,
                GtaoThickness = settings.GtaoThickness,
                GtaoFalloff = settings.GtaoFalloff,
                BentNormalMode = settings.BentNormalMode
            };

            public void ApplyTo(
                AmbientOcclusionSettings settings,
                int sourceVersion)
            {
                settings.Enabled = Enabled;
                settings.ResolutionScale = ResolutionScale;
                settings.Radius = Radius;
                settings.Intensity = Intensity;
                settings.Bias = Bias;
                settings.Power = Power;
                settings.SampleCount = SampleCount;
                settings.BlurRadius = BlurRadius;
                settings.DepthSigma = DepthSigma;
                settings.NormalSigma = NormalSigma;
                settings.UseSceneNormals = UseSceneNormals;
                settings.DebugView = Enum.IsDefined(DebugView)
                    ? DebugView
                    : AmbientOcclusionDebugView.None;
                settings.GtaoThickness = GtaoThickness;
                settings.GtaoFalloff = GtaoFalloff;
                // Schema 21 wrote SSAO/Balanced/Off even when those values were
                // only the former conservative defaults. Let the selected preset
                // own those fields during promotion. A non-SSAO request remains
                // an intentional legacy override.
                if (sourceVersion >= 22 || Mode != AmbientOcclusionMode.Ssao)
                {
                    settings.Mode = Enum.IsDefined(Mode)
                        ? Mode
                        : AmbientOcclusionMode.Ssao;
                    settings.GtaoQualityPreset = Enum.IsDefined(GtaoQualityPreset)
                        ? GtaoQualityPreset
                        : GtaoQualityPreset.Balanced;
                    settings.BentNormalMode = Enum.IsDefined(BentNormalMode)
                        ? BentNormalMode
                        : AmbientOcclusionBentNormalMode.Off;
                }
            }
        }

        private sealed record FogFile
        {
            public bool Enabled { get; init; }
            public FogTechnique Technique { get; init; } = FogTechnique.Auto;
            public FogMode Mode { get; init; } = FogMode.DistanceAndHeight;
            public FogColorMode ColorMode { get; init; } = FogColorMode.SkyAndConstantBlend;
            public Vector3File Color { get; init; } = new() { X = 0.62f, Y = 0.72f, Z = 0.82f };
            public float ColorBlend { get; init; } = 0.5f;
            public float Density { get; init; } = 0.015f;
            public float StartDistance { get; init; } = 5f;
            public float EndDistance { get; init; } = 250f;
            public float Height { get; init; }
            public float HeightFalloff { get; init; } = 0.12f;
            public float HeightDensity { get; init; } = 0.04f;
            public float MaxOpacity { get; init; } = 0.85f;
            public bool DirectionalInscatteringEnabled { get; init; } = true;
            public Vector3File DirectionalInscatteringColor { get; init; } =
                new() { X = 1f, Y = 0.88f, Z = 0.68f };
            public Vector3File DirectionalInscatteringDirection { get; init; } =
                new();
            public float DirectionalInscatteringIntensity { get; init; } = 0.35f;
            public float DirectionalInscatteringExponent { get; init; } = 8f;
            public FogDebugView DebugView { get; init; } = FogDebugView.None;
            public VolumetricFogFile Volumetric { get; init; } = new();

            public static FogFile FromSettings(FogSettings settings) => new()
            {
                Enabled = settings.Enabled,
                Technique = settings.Technique,
                Mode = settings.Mode,
                ColorMode = settings.ColorMode,
                Color = Vector3File.FromVector3(settings.Color),
                ColorBlend = settings.ColorBlend,
                Density = settings.Density,
                StartDistance = settings.StartDistance,
                EndDistance = settings.EndDistance,
                Height = settings.Height,
                HeightFalloff = settings.HeightFalloff,
                HeightDensity = settings.HeightDensity,
                MaxOpacity = settings.MaxOpacity,
                DirectionalInscatteringEnabled =
                    settings.DirectionalInscatteringEnabled,
                DirectionalInscatteringColor = Vector3File.FromVector3(
                    settings.DirectionalInscatteringColor),
                DirectionalInscatteringDirection = Vector3File.FromVector3(
                    settings.DirectionalInscatteringDirection),
                DirectionalInscatteringIntensity =
                    settings.DirectionalInscatteringIntensity,
                DirectionalInscatteringExponent =
                    settings.DirectionalInscatteringExponent,
                DebugView = settings.DebugView,
                Volumetric = VolumetricFogFile.FromSettings(settings.Volumetric)
            };

            public void ApplyTo(FogSettings settings)
            {
                settings.Enabled = Enabled;
                settings.Technique = Enum.IsDefined(Technique) ? Technique : FogTechnique.Auto;
                settings.Mode = Enum.IsDefined(Mode) ? Mode : FogMode.DistanceAndHeight;
                settings.ColorMode = Enum.IsDefined(ColorMode) ? ColorMode : FogColorMode.SkyAndConstantBlend;
                settings.Color = Color.ToVector3();
                settings.ColorBlend = ColorBlend;
                settings.Density = Density;
                settings.StartDistance = StartDistance;
                settings.EndDistance = EndDistance;
                settings.Height = Height;
                settings.HeightFalloff = HeightFalloff;
                settings.HeightDensity = HeightDensity;
                settings.MaxOpacity = MaxOpacity;
                settings.DirectionalInscatteringEnabled =
                    DirectionalInscatteringEnabled;
                settings.DirectionalInscatteringColor =
                    DirectionalInscatteringColor.ToVector3();
                settings.DirectionalInscatteringDirection =
                    DirectionalInscatteringDirection.ToVector3();
                settings.DirectionalInscatteringIntensity =
                    DirectionalInscatteringIntensity;
                settings.DirectionalInscatteringExponent =
                    DirectionalInscatteringExponent;
                settings.DebugView = Enum.IsDefined(DebugView)
                    ? DebugView
                    : FogDebugView.None;
                Volumetric.ApplyTo(settings.Volumetric);
            }
        }

        private sealed record VolumetricFogFile
        {
            // Kept only so version-16 files deserialize without an unknown
            // member. Qualification is deliberately not restored from disk.
            public bool? ProfileQualified { get; init; }
            public float MaxDistance { get; init; } = 250f;
            public float BaseExtinctionPerMeter { get; init; } = 0.015f;
            public float HeightExtinctionPerMeter { get; init; } = 0.04f;
            public float Height { get; init; }
            public float HeightFalloff { get; init; } = 0.12f;
            public Vector3File ScatteringAlbedo { get; init; } = new() { X = 0.9f, Y = 0.92f, Z = 0.95f };
            public float Anisotropy { get; init; } = 0.2f;
            public Vector3File GlobalWind { get; init; } = new();
            public float NoiseScale { get; init; } = 0.035f;
            public float NoiseStrength { get; init; } = 0.15f;
            public float NoiseContrast { get; init; } = 1f;
            public float SelfShadowDistance { get; init; } = 64f;
            public float TemporalHistoryWeight { get; init; } = 0.9f;
            public int MultipleScatteringIterations { get; init; }
            public float MultipleScatteringEnergyLimit { get; init; } = 0.5f;
            public int DebugSlice { get; init; } = -1;
            public FogDebugProjection DebugProjection { get; init; } =
                FogDebugProjection.MaxAlongRay;

            public static VolumetricFogFile FromSettings(VolumetricFogSettings settings) => new()
            {
                MaxDistance = settings.MaxDistance,
                BaseExtinctionPerMeter = settings.BaseExtinctionPerMeter,
                HeightExtinctionPerMeter = settings.HeightExtinctionPerMeter,
                Height = settings.Height,
                HeightFalloff = settings.HeightFalloff,
                ScatteringAlbedo = Vector3File.FromVector3(settings.ScatteringAlbedo),
                Anisotropy = settings.Anisotropy,
                GlobalWind = Vector3File.FromVector3(settings.GlobalWind),
                NoiseScale = settings.NoiseScale,
                NoiseStrength = settings.NoiseStrength,
                NoiseContrast = settings.NoiseContrast,
                SelfShadowDistance = settings.SelfShadowDistance,
                TemporalHistoryWeight = settings.TemporalHistoryWeight,
                MultipleScatteringIterations = settings.MultipleScatteringIterations,
                MultipleScatteringEnergyLimit = settings.MultipleScatteringEnergyLimit,
                DebugSlice = settings.DebugSlice,
                DebugProjection = settings.DebugProjection
            };

            public void ApplyTo(VolumetricFogSettings settings)
            {
                settings.MaxDistance = MaxDistance;
                settings.BaseExtinctionPerMeter = BaseExtinctionPerMeter;
                settings.HeightExtinctionPerMeter = HeightExtinctionPerMeter;
                settings.Height = Height;
                settings.HeightFalloff = HeightFalloff;
                settings.ScatteringAlbedo = ScatteringAlbedo.ToVector3();
                settings.Anisotropy = Anisotropy;
                settings.GlobalWind = GlobalWind.ToVector3();
                settings.NoiseScale = NoiseScale;
                settings.NoiseStrength = NoiseStrength;
                settings.NoiseContrast = NoiseContrast;
                settings.SelfShadowDistance = SelfShadowDistance;
                settings.TemporalHistoryWeight = TemporalHistoryWeight;
                settings.MultipleScatteringIterations = MultipleScatteringIterations;
                settings.MultipleScatteringEnergyLimit = MultipleScatteringEnergyLimit;
                settings.DebugSlice = DebugSlice;
                settings.DebugProjection = Enum.IsDefined(DebugProjection)
                    ? DebugProjection
                    : FogDebugProjection.MaxAlongRay;
            }
        }

        private sealed record ReflectionSettingsFile
        {
            public bool? CaptureLodEnabled { get; init; }
            public float? CaptureLodTargetPixelError { get; init; }
            public bool Enabled { get; init; } = true;
            public ReflectionMode Mode { get; init; } = ReflectionMode.StaticProbes;
            public ReflectionImplementationMode ImplementationMode { get; init; } =
                ReflectionImplementationMode.Auto;
            public int MaxProbes { get; init; } = 8;
            public int MaxProbesPerPixel { get; init; } = 2;
            public uint ProbeResolution { get; init; } = 128;
            public float Intensity { get; init; } = 1.0f;
            public float GlobalFallbackIntensity { get; init; } = 1.0f;
            public bool BoxProjectionEnabled { get; init; } = true;
            public bool ProbeBlendingEnabled { get; init; } = true;
            public float SsrFullResolutionRoughness { get; init; } = 0.2f;
            public float SsrHalfResolutionRoughness { get; init; } = 0.5f;
            public float SsrQuarterResolutionRoughness { get; init; } = 0.8f;
            public int SsrMaxSteps { get; init; } = 64;
            public float SsrMaxDistance { get; init; } = 75.0f;
            public float SsrConfidenceThreshold { get; init; } = 0.75f;
            public float RayQueryPixelBudgetFraction { get; init; } = 0.0078125f;
            public int RayQueryHitLightLimit { get; init; } = 2;
            public int TemporalHistoryLength { get; init; } = 16;
            public ReflectionDenoiser Denoiser { get; init; } = ReflectionDenoiser.Amd;
            public bool AmdHalfResolution { get; init; } = true;
            public int SpatialFilterPassCount { get; init; } = 2;

            public static ReflectionSettingsFile FromSettings(ReflectionSettings settings) => new()
            {
                CaptureLodEnabled = settings.CaptureLodEnabled,
                CaptureLodTargetPixelError = settings.CaptureLodTargetPixelError,
                Enabled = settings.Enabled,
                Mode = settings.Mode,
                ImplementationMode = settings.ImplementationMode,
                MaxProbes = settings.MaxProbes,
                MaxProbesPerPixel = settings.MaxProbesPerPixel,
                ProbeResolution = settings.ProbeResolution,
                Intensity = settings.Intensity,
                GlobalFallbackIntensity = settings.GlobalFallbackIntensity,
                BoxProjectionEnabled = settings.BoxProjectionEnabled,
                ProbeBlendingEnabled = settings.ProbeBlendingEnabled,
                SsrFullResolutionRoughness = settings.SsrFullResolutionRoughness,
                SsrHalfResolutionRoughness = settings.SsrHalfResolutionRoughness,
                SsrQuarterResolutionRoughness = settings.SsrQuarterResolutionRoughness,
                SsrMaxSteps = settings.SsrMaxSteps,
                SsrMaxDistance = settings.SsrMaxDistance,
                SsrConfidenceThreshold = settings.SsrConfidenceThreshold,
                RayQueryPixelBudgetFraction = settings.RayQueryPixelBudgetFraction,
                RayQueryHitLightLimit = settings.RayQueryHitLightLimit,
                TemporalHistoryLength = settings.TemporalHistoryLength,
                Denoiser = settings.Denoiser,
                AmdHalfResolution = settings.AmdHalfResolution,
                SpatialFilterPassCount = settings.SpatialFilterPassCount
            };

            public void ApplyTo(ReflectionSettings settings)
            {
                // Missing fields inherit the preset already applied by the parent settings file.
                settings.CaptureLodEnabled = CaptureLodEnabled ?? settings.CaptureLodEnabled;
                settings.CaptureLodTargetPixelError = CaptureLodTargetPixelError ?? settings.CaptureLodTargetPixelError;
                settings.Enabled = Enabled;
                settings.Mode = Enum.IsDefined(Mode) ? Mode : ReflectionMode.StaticProbes;
                settings.ImplementationMode = Enum.IsDefined(ImplementationMode)
                    ? ImplementationMode
                    : ReflectionImplementationMode.Auto;
                settings.MaxProbes = MaxProbes;
                settings.MaxProbesPerPixel = MaxProbesPerPixel;
                settings.ProbeResolution = ProbeResolution;
                settings.Intensity = Intensity;
                settings.GlobalFallbackIntensity = GlobalFallbackIntensity;
                settings.BoxProjectionEnabled = BoxProjectionEnabled;
                settings.ProbeBlendingEnabled = ProbeBlendingEnabled;
                settings.SsrFullResolutionRoughness = SsrFullResolutionRoughness;
                settings.SsrHalfResolutionRoughness = SsrHalfResolutionRoughness;
                settings.SsrQuarterResolutionRoughness = SsrQuarterResolutionRoughness;
                settings.SsrMaxSteps = SsrMaxSteps;
                settings.SsrMaxDistance = SsrMaxDistance;
                settings.SsrConfidenceThreshold = SsrConfidenceThreshold;
                settings.RayQueryPixelBudgetFraction = RayQueryPixelBudgetFraction;
                settings.RayQueryHitLightLimit = RayQueryHitLightLimit;
                settings.TemporalHistoryLength = TemporalHistoryLength;
                settings.Denoiser = Denoiser;
                settings.AmdHalfResolution = AmdHalfResolution;
                settings.SpatialFilterPassCount = SpatialFilterPassCount;
            }
        }

        private sealed record TransparencySettingsFile
        {
            public bool Enabled { get; init; } = true;
            public TransparencyMode Mode { get; init; } =
                TransparencyMode.SortedAlphaBlend;
            public TransparencyDebugView DebugView { get; init; }
            public bool ReceiveShadows { get; init; } = true;
            public bool ReceiveGlobalIllumination { get; init; } = true;
            public bool SampleReflections { get; init; } = true;
            public int? SceneReflectionRayTaskBudget { get; init; }
            public int? SceneReflectionSsrSampleBudget { get; init; }
            public bool SortPerMeshlet { get; init; } = true;
            public bool? PipelinePartitioningEnabled { get; init; }
            public int MaxTransparentMeshlets { get; init; } = 262_144;
            public float AlphaDiscardThreshold { get; init; } = 0.001f;
            public ThickTransmissionMode ThickTransmissionMode { get; init; } =
                ThickTransmissionMode.RayQuery;
            public DispersionMode DispersionMode { get; init; } =
                DispersionMode.Off;
            public int ThickTransmissionRayTaskBudget { get; init; } = 262_144;
            public int ThickTransmissionMaximumInterfaces { get; init; } =
                BoundedDielectricMediaStack.MaximumInterfaces;
            public int ThickTransmissionMaximumMediaDepth { get; init; } =
                BoundedDielectricMediaStack.MaximumDepth;
            public int ThickTransmissionMaximumCandidatesPerInterface
            {
                get;
                init;
            } = BoundedDielectricMediaStack.MaximumCandidatesPerInterface;
            public float ThickTransmissionMaximumDistance { get; init; } = 100f;
            public ulong ThickTransmissionMemoryBudgetBytes { get; init; } =
                128UL * 1024UL * 1024UL;

            public static TransparencySettingsFile FromSettings(
                TransparencySettings settings) => new()
            {
                Enabled = settings.Enabled,
                Mode = settings.Mode,
                DebugView = settings.DebugView,
                ReceiveShadows = settings.ReceiveShadows,
                ReceiveGlobalIllumination = settings.ReceiveGlobalIllumination,
                SampleReflections = settings.SampleReflections,
                SceneReflectionRayTaskBudget =
                    settings.SceneReflectionRayTaskBudget,
                SceneReflectionSsrSampleBudget =
                    settings.SceneReflectionSsrSampleBudget,
                SortPerMeshlet = settings.SortPerMeshlet,
                PipelinePartitioningEnabled =
                    settings.PipelinePartitioningEnabled,
                MaxTransparentMeshlets = settings.MaxTransparentMeshlets,
                AlphaDiscardThreshold = settings.AlphaDiscardThreshold,
                ThickTransmissionMode = settings.ThickTransmissionMode,
                DispersionMode = settings.DispersionMode,
                ThickTransmissionRayTaskBudget =
                    settings.ThickTransmissionRayTaskBudget,
                ThickTransmissionMaximumInterfaces =
                    settings.ThickTransmissionMaximumInterfaces,
                ThickTransmissionMaximumMediaDepth =
                    settings.ThickTransmissionMaximumMediaDepth,
                ThickTransmissionMaximumCandidatesPerInterface =
                    settings.ThickTransmissionMaximumCandidatesPerInterface,
                ThickTransmissionMaximumDistance =
                    settings.ThickTransmissionMaximumDistance,
                ThickTransmissionMemoryBudgetBytes =
                    settings.ThickTransmissionMemoryBudgetBytes
            };

            public void ApplyTo(
                TransparencySettings settings,
                int sourceVersion)
            {
                settings.Enabled = Enabled;
                settings.Mode = Enum.IsDefined(Mode)
                    ? Mode : TransparencyMode.SortedAlphaBlend;
                settings.DebugView = Enum.IsDefined(DebugView)
                    ? DebugView : TransparencyDebugView.None;
                settings.ReceiveShadows = ReceiveShadows;
                settings.ReceiveGlobalIllumination = ReceiveGlobalIllumination;
                settings.SampleReflections = SampleReflections;
                if (SceneReflectionRayTaskBudget.HasValue)
                {
                    settings.SceneReflectionRayTaskBudget =
                        SceneReflectionRayTaskBudget.Value;
                }
                if (SceneReflectionSsrSampleBudget.HasValue)
                {
                    settings.SceneReflectionSsrSampleBudget =
                        SceneReflectionSsrSampleBudget.Value;
                }
                settings.SortPerMeshlet = SortPerMeshlet;
                if (PipelinePartitioningEnabled == true ||
                    sourceVersion >= 22 && PipelinePartitioningEnabled.HasValue)
                {
                    settings.PipelinePartitioningEnabled =
                        PipelinePartitioningEnabled.Value;
                }
                settings.MaxTransparentMeshlets = MaxTransparentMeshlets;
                settings.AlphaDiscardThreshold = AlphaDiscardThreshold;
                settings.ThickTransmissionMode =
                    Enum.IsDefined(ThickTransmissionMode)
                        ? ThickTransmissionMode
                        : ThickTransmissionMode.Approximation;
                settings.DispersionMode = Enum.IsDefined(DispersionMode)
                    ? DispersionMode : DispersionMode.Off;
                settings.ThickTransmissionRayTaskBudget =
                    ThickTransmissionRayTaskBudget;
                settings.ThickTransmissionMaximumInterfaces =
                    ThickTransmissionMaximumInterfaces;
                settings.ThickTransmissionMaximumMediaDepth =
                    ThickTransmissionMaximumMediaDepth;
                settings.ThickTransmissionMaximumCandidatesPerInterface =
                    ThickTransmissionMaximumCandidatesPerInterface;
                settings.ThickTransmissionMaximumDistance =
                    ThickTransmissionMaximumDistance;
                settings.ThickTransmissionMemoryBudgetBytes =
                    ThickTransmissionMemoryBudgetBytes;
            }
        }

        private sealed record ShadowSettingsFile
        {
            public bool? PointShadowsEnabled { get; init; }
            public bool? SpotShadowsEnabled { get; init; }
            public int? MaxShadowedPointLights { get; init; }
            public int? MaxShadowedSpotLights { get; init; }
            public uint? PointShadowMapSize { get; init; }
            public uint? SpotShadowTileSize { get; init; }
            public uint? SpotShadowAtlasSize { get; init; }
            public int? LocalShadowMemoryBudgetMiB { get; init; }
            public bool? LocalShadowCacheEnabled { get; init; }
            public float? PointNormalBias { get; init; }
            public float? PointConstantDepthBias { get; init; }
            public float? PointSlopeScaledDepthBias { get; init; }
            public int? PointPcfRadius { get; init; }
            public float? SpotNormalBias { get; init; }
            public float? SpotConstantDepthBias { get; init; }
            public float? SpotSlopeScaledDepthBias { get; init; }
            public int? SpotPcfRadius { get; init; }
            public bool DirectionalShadowsEnabled { get; init; } = true;
            public DirectionalShadowMode RequestedDirectionalShadowMode { get; init; } =
                DirectionalShadowMode.Cascaded;
            public DirectionalCsmTemporalMode DirectionalCsmTemporalMode { get; init; } =
                DirectionalCsmTemporalMode.Disabled;
            public DirectionalShadowFilterMode DirectionalFilterMode { get; init; } =
                DirectionalShadowFilterMode.LegacyBoxPcf;
            public DirectionalShadowBiasMode DirectionalBiasMode { get; init; } =
                DirectionalShadowBiasMode.Legacy;
            public DirectionalPcfRadiusMode DirectionalPcfRadiusMode { get; init; } =
                DirectionalPcfRadiusMode.Constant;
            public uint DirectionalShadowMapSize { get; init; } = 2048;
            public int DirectionalCascadeCount { get; init; } = 2;
            public float MaxShadowDistance { get; init; } = 80f;
            public float DirectionalCascadeBlendFraction { get; init; } = 0.12f;
            public float DirectionalCascadeSplitLambda { get; init; } = 0.5f;
            public float DirectionalCasterExtrusionDistance { get; init; } = 80f;
            public float DirectionalContactShadowDistance { get; init; } = 3f;
            public float DirectionalSoftAngularDiameterScale { get; init; } = 1f;
            public int DirectionalSoftRecoveryRayCount { get; init; } = 2;
            public int DirectionalSoftHistoryLength { get; init; } = 16;
            public int DirectionalSoftSpatialPassCount { get; init; } = 3;
            public int DirectionalTransparentSoftRayCount { get; init; } = 4;
            public float NormalBias { get; init; } = 0.03f;
            public float SlopeScaledDepthBias { get; init; } = 1.5f;
            public float ConstantDepthBias { get; init; } = 0.0005f;
            public int PcfRadius { get; init; } = 1;
            public bool AreaShadowsEnabled { get; init; } = true;
            public bool AreaDenoisingEnabled { get; init; }
            public int MaxShadowedAreaLights { get; init; } = 2;
            public int AreaShadowSampleCount { get; init; } = 1;

            public static ShadowSettingsFile FromSettings(ShadowSettings settings) => new()
            {
                PointShadowsEnabled = settings.PointShadowsEnabled,
                SpotShadowsEnabled = settings.SpotShadowsEnabled,
                MaxShadowedPointLights = settings.MaxShadowedPointLights,
                MaxShadowedSpotLights = settings.MaxShadowedSpotLights,
                PointShadowMapSize = settings.PointShadowMapSize,
                SpotShadowTileSize = settings.SpotShadowTileSize,
                SpotShadowAtlasSize = settings.SpotShadowAtlasSize,
                LocalShadowMemoryBudgetMiB = settings.LocalShadowMemoryBudgetMiB,
                LocalShadowCacheEnabled = settings.LocalShadowCacheEnabled,
                PointNormalBias = settings.PointNormalBias,
                PointConstantDepthBias = settings.PointConstantDepthBias,
                PointSlopeScaledDepthBias = settings.PointSlopeScaledDepthBias,
                PointPcfRadius = settings.PointPcfRadius,
                SpotNormalBias = settings.SpotNormalBias,
                SpotConstantDepthBias = settings.SpotConstantDepthBias,
                SpotSlopeScaledDepthBias = settings.SpotSlopeScaledDepthBias,
                SpotPcfRadius = settings.SpotPcfRadius,

                DirectionalShadowsEnabled = settings.DirectionalShadowsEnabled,
                RequestedDirectionalShadowMode = settings.RequestedDirectionalShadowMode,
                DirectionalCsmTemporalMode = settings.DirectionalCsmTemporalMode,
                DirectionalFilterMode = settings.DirectionalFilterMode,
                DirectionalBiasMode = settings.DirectionalBiasMode,
                DirectionalPcfRadiusMode = settings.DirectionalPcfRadiusMode,
                DirectionalShadowMapSize = settings.DirectionalShadowMapSize,
                DirectionalCascadeCount = settings.DirectionalCascadeCount,
                MaxShadowDistance = settings.MaxShadowDistance,
                DirectionalCascadeBlendFraction = settings.DirectionalCascadeBlendFraction,
                DirectionalCascadeSplitLambda = settings.DirectionalCascadeSplitLambda,
                DirectionalCasterExtrusionDistance = settings.DirectionalCasterExtrusionDistance,
                DirectionalContactShadowDistance = settings.DirectionalContactShadowDistance,
                DirectionalSoftAngularDiameterScale = settings.DirectionalSoftAngularDiameterScale,
                DirectionalSoftRecoveryRayCount = settings.DirectionalSoftRecoveryRayCount,
                DirectionalSoftHistoryLength = settings.DirectionalSoftHistoryLength,
                DirectionalSoftSpatialPassCount = settings.DirectionalSoftSpatialPassCount,
                DirectionalTransparentSoftRayCount = settings.DirectionalTransparentSoftRayCount,
                NormalBias = settings.NormalBias,
                SlopeScaledDepthBias = settings.SlopeScaledDepthBias,
                ConstantDepthBias = settings.ConstantDepthBias,
                PcfRadius = settings.PcfRadius,
                AreaShadowsEnabled = settings.AreaShadowsEnabled,
                AreaDenoisingEnabled = settings.AreaDenoisingEnabled,
                MaxShadowedAreaLights = settings.MaxShadowedAreaLights,
                AreaShadowSampleCount = settings.AreaShadowSampleCount
            };

            public void ApplyTo(
                ShadowSettings settings,
                int sourceVersion)
            {
                if (PointShadowsEnabled.HasValue) settings.PointShadowsEnabled = PointShadowsEnabled.Value;
                if (SpotShadowsEnabled.HasValue) settings.SpotShadowsEnabled = SpotShadowsEnabled.Value;
                if (MaxShadowedPointLights.HasValue) settings.MaxShadowedPointLights = MaxShadowedPointLights.Value;
                if (MaxShadowedSpotLights.HasValue) settings.MaxShadowedSpotLights = MaxShadowedSpotLights.Value;
                if (PointShadowMapSize.HasValue) settings.PointShadowMapSize = PointShadowMapSize.Value;
                if (SpotShadowTileSize.HasValue) settings.SpotShadowTileSize = SpotShadowTileSize.Value;
                if (SpotShadowAtlasSize.HasValue) settings.SpotShadowAtlasSize = SpotShadowAtlasSize.Value;
                if (LocalShadowMemoryBudgetMiB.HasValue) settings.LocalShadowMemoryBudgetMiB = LocalShadowMemoryBudgetMiB.Value;
                if (LocalShadowCacheEnabled.HasValue) settings.LocalShadowCacheEnabled = LocalShadowCacheEnabled.Value;
                if (PointNormalBias.HasValue) settings.PointNormalBias = PointNormalBias.Value;
                if (PointConstantDepthBias.HasValue) settings.PointConstantDepthBias = PointConstantDepthBias.Value;
                if (PointSlopeScaledDepthBias.HasValue) settings.PointSlopeScaledDepthBias = PointSlopeScaledDepthBias.Value;
                if (PointPcfRadius.HasValue) settings.PointPcfRadius = PointPcfRadius.Value;
                if (SpotNormalBias.HasValue) settings.SpotNormalBias = SpotNormalBias.Value;
                if (SpotConstantDepthBias.HasValue) settings.SpotConstantDepthBias = SpotConstantDepthBias.Value;
                if (SpotSlopeScaledDepthBias.HasValue) settings.SpotSlopeScaledDepthBias = SpotSlopeScaledDepthBias.Value;
                if (SpotPcfRadius.HasValue) settings.SpotPcfRadius = SpotPcfRadius.Value;
                settings.DirectionalShadowsEnabled = DirectionalShadowsEnabled;
                settings.RequestedDirectionalShadowMode = RequestedDirectionalShadowMode;
                settings.DirectionalCsmTemporalMode =
                    sourceVersion < 25 && DirectionalCsmTemporalMode ==
                        global::Njulf.Rendering.Data
                            .DirectionalCsmTemporalMode.Auto
                        ? global::Njulf.Rendering.Data
                            .DirectionalCsmTemporalMode.Enabled
                        : DirectionalCsmTemporalMode;
                settings.DirectionalFilterMode = DirectionalFilterMode;
                settings.DirectionalBiasMode = DirectionalBiasMode;
                settings.DirectionalPcfRadiusMode = DirectionalPcfRadiusMode;
                settings.DirectionalShadowMapSize = DirectionalShadowMapSize;
                settings.DirectionalCascadeCount = DirectionalCascadeCount;
                settings.MaxShadowDistance = MaxShadowDistance;
                settings.DirectionalCascadeBlendFraction = DirectionalCascadeBlendFraction;
                settings.DirectionalCascadeSplitLambda = DirectionalCascadeSplitLambda;
                settings.DirectionalCasterExtrusionDistance = DirectionalCasterExtrusionDistance;
                settings.DirectionalContactShadowDistance = DirectionalContactShadowDistance;
                settings.DirectionalSoftAngularDiameterScale = DirectionalSoftAngularDiameterScale;
                settings.DirectionalSoftRecoveryRayCount = DirectionalSoftRecoveryRayCount;
                settings.DirectionalSoftHistoryLength = DirectionalSoftHistoryLength;
                settings.DirectionalSoftSpatialPassCount = DirectionalSoftSpatialPassCount;
                settings.DirectionalTransparentSoftRayCount = DirectionalTransparentSoftRayCount;
                settings.NormalBias = NormalBias;
                settings.SlopeScaledDepthBias = SlopeScaledDepthBias;
                settings.ConstantDepthBias = ConstantDepthBias;
                settings.PcfRadius = PcfRadius;
                settings.AreaShadowsEnabled = AreaShadowsEnabled;
                settings.AreaDenoisingEnabled = AreaDenoisingEnabled;
                settings.MaxShadowedAreaLights = MaxShadowedAreaLights;
                settings.AreaShadowSampleCount = AreaShadowSampleCount;
            }
        }

        private sealed record EnvironmentFile
        {
            public bool Enabled { get; init; } = true;
            public EnvironmentSourceKind SourceKind { get; init; } =
                EnvironmentSourceKind.ProceduralSky;
            public string? SourcePath { get; init; }
            public EnvironmentTexturePrecision TexturePrecision { get; init; } =
                EnvironmentTexturePrecision.Float16;
            public ProceduralSkySunDriver SunDriver { get; init; } =
                ProceduralSkySunDriver.SceneDirectionalLight;
            public bool AnimateTimeOfDay { get; init; }
            public float Turbidity { get; init; } = 3.0f;
            public float GroundAlbedoX { get; init; } = 0.2f;
            public float GroundAlbedoY { get; init; } = 0.2f;
            public float GroundAlbedoZ { get; init; } = 0.2f;
            public float SunAngularDiameterDegrees { get; init; } = 0.53f;
            public float MoonAngularDiameterDegrees { get; init; } = 0.52f;
            public float TimeOfDayHours { get; init; } = 14.0f;
            public float LatitudeDegrees { get; init; } = 59.9139f;
            public int DayOfYear { get; init; } = 172;
            public float NorthOffsetDegrees { get; init; }
            public float TimeScale { get; init; } = 60.0f;
            public float DirectSunDirectionX { get; init; } = -0.3010859f;
            public float DirectSunDirectionY { get; init; } = 0.7226061f;
            public float DirectSunDirectionZ { get; init; } = 0.6222441f;
            public float AtmosphereIntensity { get; init; } = 1.0f;
            public float SolarIrradianceScale { get; init; } = 14.0f;
            public float MoonIrradianceScale { get; init; } = 0.12f;
            public float StarIntensity { get; init; } = 0.025f;
            public float AirglowIntensity { get; init; } = 0.025f;
            public float GiSunStepDegrees { get; init; } = 0.25f;
            public float GiTargetSourceSweepSeconds { get; init; } = 8.0f;
            public int SpecularPrefilterMipsPerFrame { get; init; } = 1;
            public int SpecularPrefilterTransitionFrames { get; init; } = 8;
            public float SkyIntensity { get; init; } = 1.0f;
            public float DiffuseIntensity { get; init; } = 1.0f;
            public float SpecularIntensity { get; init; } = 1.0f;
            public float RotationRadians { get; init; }
            public uint EnvironmentSize { get; init; } = 1024;
            public uint IrradianceSize { get; init; } = 64;
            public uint PrefilteredSize { get; init; } = 128;
            public uint BrdfLutSize { get; init; } = 256;
            public EnvironmentDebugView DebugView { get; init; } =
                EnvironmentDebugView.None;
            public int DebugMipLevel { get; init; }

            public static EnvironmentFile FromSettings(EnvironmentSettings settings)
            {
                Vector3 ground = settings.GroundAlbedo;
                Vector3 sun = settings.DirectSunDirection;
                return new EnvironmentFile
                {
                    Enabled = settings.Enabled,
                    SourceKind = settings.SourceKind,
                    SourcePath = settings.SourcePath,
                    TexturePrecision = settings.TexturePrecision,
                    SunDriver = settings.SunDriver,
                    AnimateTimeOfDay = settings.AnimateTimeOfDay,
                    Turbidity = settings.Turbidity,
                    GroundAlbedoX = ground.X,
                    GroundAlbedoY = ground.Y,
                    GroundAlbedoZ = ground.Z,
                    SunAngularDiameterDegrees = settings.SunAngularDiameterDegrees,
                    MoonAngularDiameterDegrees = settings.MoonAngularDiameterDegrees,
                    TimeOfDayHours = settings.TimeOfDayHours,
                    LatitudeDegrees = settings.LatitudeDegrees,
                    DayOfYear = settings.DayOfYear,
                    NorthOffsetDegrees = settings.NorthOffsetDegrees,
                    TimeScale = settings.TimeScale,
                    DirectSunDirectionX = sun.X,
                    DirectSunDirectionY = sun.Y,
                    DirectSunDirectionZ = sun.Z,
                    AtmosphereIntensity = settings.AtmosphereIntensity,
                    SolarIrradianceScale = settings.SolarIrradianceScale,
                    MoonIrradianceScale = settings.MoonIrradianceScale,
                    StarIntensity = settings.StarIntensity,
                    AirglowIntensity = settings.AirglowIntensity,
                    GiSunStepDegrees = settings.GiSunStepDegrees,
                    GiTargetSourceSweepSeconds = settings.GiTargetSourceSweepSeconds,
                    SpecularPrefilterMipsPerFrame = settings.SpecularPrefilterMipsPerFrame,
                    SpecularPrefilterTransitionFrames = settings.SpecularPrefilterTransitionFrames,
                    SkyIntensity = settings.SkyIntensity,
                    DiffuseIntensity = settings.DiffuseIntensity,
                    SpecularIntensity = settings.SpecularIntensity,
                    RotationRadians = settings.RotationRadians,
                    EnvironmentSize = settings.EnvironmentSize,
                    IrradianceSize = settings.IrradianceSize,
                    PrefilteredSize = settings.PrefilteredSize,
                    BrdfLutSize = settings.BrdfLutSize,
                    DebugView = settings.DebugView,
                    DebugMipLevel = settings.DebugMipLevel
                };
            }

            public void ApplyTo(EnvironmentSettings settings)
            {
                settings.Enabled = Enabled;
                settings.SourceKind = SourceKind;
                settings.SourcePath = SourcePath;
                settings.TexturePrecision = TexturePrecision;
                settings.SunDriver = SunDriver;
                settings.AnimateTimeOfDay = AnimateTimeOfDay;
                settings.Turbidity = Turbidity;
                settings.GroundAlbedo = new Vector3(
                    GroundAlbedoX,
                    GroundAlbedoY,
                    GroundAlbedoZ);
                settings.SunAngularDiameterDegrees = SunAngularDiameterDegrees;
                settings.MoonAngularDiameterDegrees = MoonAngularDiameterDegrees;
                settings.TimeOfDayHours = TimeOfDayHours;
                settings.LatitudeDegrees = LatitudeDegrees;
                settings.DayOfYear = DayOfYear;
                settings.NorthOffsetDegrees = NorthOffsetDegrees;
                settings.TimeScale = TimeScale;
                settings.DirectSunDirection = new Vector3(
                    DirectSunDirectionX,
                    DirectSunDirectionY,
                    DirectSunDirectionZ);
                settings.AtmosphereIntensity = AtmosphereIntensity;
                settings.SolarIrradianceScale = SolarIrradianceScale;
                settings.MoonIrradianceScale = MoonIrradianceScale;
                settings.StarIntensity = StarIntensity;
                settings.AirglowIntensity = AirglowIntensity;
                settings.GiSunStepDegrees = GiSunStepDegrees;
                settings.GiTargetSourceSweepSeconds = GiTargetSourceSweepSeconds;
                settings.SpecularPrefilterMipsPerFrame = SpecularPrefilterMipsPerFrame;
                settings.SpecularPrefilterTransitionFrames =
                    SpecularPrefilterTransitionFrames;
                settings.SkyIntensity = SkyIntensity;
                settings.DiffuseIntensity = DiffuseIntensity;
                settings.SpecularIntensity = SpecularIntensity;
                settings.RotationRadians = RotationRadians;
                settings.EnvironmentSize = EnvironmentSize;
                settings.IrradianceSize = IrradianceSize;
                settings.PrefilteredSize = PrefilteredSize;
                settings.BrdfLutSize = BrdfLutSize;
                settings.DebugView = DebugView;
                settings.DebugMipLevel = DebugMipLevel;
            }
        }

        private sealed record GlobalIlluminationFile
        {
            public bool Enabled { get; init; } = true;
            public GlobalIlluminationMode Mode { get; init; } = GlobalIlluminationMode.Ddgi;
            public GlobalIlluminationDebugView DebugView { get; init; } = GlobalIlluminationDebugView.None;
            public bool EmergencyGiFallbackEnabled { get; init; }
            public float IndirectIntensity { get; init; } = 1.0f;
            public float EnvironmentFallbackIntensity { get; init; } = 1.0f;
            // Keep partially specified settings files on the DDGI-focused default.
            public bool UseDdgi { get; init; } = true;
            // Nullable preserves quality-preset defaults for legacy files that
            // predate the explicit backend selector. A serialized false remains
            // an explicit opt-out.
            public bool? UseRayQueryBackend { get; init; }
            public DdgiQualityTier DdgiQualityTier { get; init; } = DdgiQualityTier.DdgiHigh;
            public bool GiMaterialTransportV2 { get; init; }
            public bool GiEmissiveMeshSampling { get; init; }
            public bool DdgiProbeClassificationEnabled { get; init; } = true;
            public bool DdgiProbeRelocationEnabled { get; init; }
            public bool DdgiProbeL1MetadataEnabled { get; init; } = true;
            public bool DdgiCameraRelativeEnabled { get; init; } = true;
            public bool DdgiAdaptiveBudgetingEnabled { get; init; } = true;
            public bool DdgiThinWallPolicyEnabled { get; init; } = true;
            public bool DdgiAsyncComputeEnabled { get; init; } = true;
            public bool DdgiAlphaMaskedTransportEnabled { get; init; } = true;
            // Nullable preserves the authored default for older settings files
            // while allowing an explicit CPU/mirror/resident value to round-trip.
            public SimpleDdgiSchedulerMode? SimpleDdgiSchedulerMode { get; init; }
            public int? SimpleDdgiSchedulerReentryStableFrameCount { get; init; }
            public SimpleDdgiProbeResidencyMode? SimpleDdgiProbeResidencyMode { get; init; }
            public int? SimpleDdgiSparsePhysicalPageBudget { get; init; }
            public int? SimpleDdgiSparseMinimumPhysicalPageBudget { get; init; }
            public int? SimpleDdgiSparseRetentionFrames { get; init; }
            public int? SimpleDdgiSparseMaximumAdmissionsPerFrame { get; init; }
            public int? SimpleDdgiSparseMaximumReceiverFeedbackRequests { get; init; }
            public int? SimpleDdgiSparseInactiveRetryFrames { get; init; }
            public SimpleDdgiAuthoredVolumeFile[] SimpleDdgiAuthoredVolumes { get; init; } = Array.Empty<SimpleDdgiAuthoredVolumeFile>();
            public bool SimpleDdgiSharedMemoryBlendEnabled { get; init; } = true;
            public bool SimpleDdgiClassificationSchedulingEnabled { get; init; } = true;
            public bool SimpleDdgiCostAwareSchedulingEnabled { get; init; } = true;
            public bool SimpleDdgiReceiverContributionFeedbackEnabled { get; init; } = true;
            public bool SimpleDdgiPersistentWarmStartEnabled { get; init; } = true;
            public bool SimpleDdgiSparseResidualPropagationEnabled { get; init; } = true;
            public bool SimpleDdgiUrgentRelightEnabled { get; init; } = true;
            public int? SimpleDdgiUrgentRelightProbeBudget { get; init; }
            public SimpleDdgiSourceCacheLayoutMode SimpleDdgiSourceCacheLayoutMode { get; init; } =
                SimpleDdgiSourceCacheLayoutMode.FixedRecord;
            public bool SimpleDdgiClassificationReadbackEnabled { get; init; } = true;
            public bool SimpleDdgiAdaptiveHysteresisEnabled { get; init; } = true;
            public bool SimpleDdgiLightingDirtyBoostEnabled { get; init; } = true;
            public bool SimpleDdgiDynamicGeometryDirtyBoostEnabled { get; init; } = true;
            public bool SimpleDdgiAdaptiveRaysEnabled { get; init; } = true;
            public bool SimpleDdgiTransportV2Enabled { get; init; } = true;
            public bool SimpleDdgiThinSurfaceTransmissionEnabled { get; init; }
            public bool SimpleDdgiAutomaticProbeDensityEnabled { get; init; } = true;
            public bool SimpleDdgiStructuredGatherEnabled { get; init; } = true;
            public SimpleDdgiReceiverCacheMode? SimpleDdgiReceiverCacheMode
            {
                get;
                init;
            }
            public SimpleDdgiLayoutAdmissionMode SimpleDdgiLayoutAdmissionMode { get; init; } = SimpleDdgiLayoutAdmissionMode.Degrade;
            public bool SimpleDdgiReducedBlendEnabled { get; init; }
            // Nullable representation controls preserve the selected quality
            // tier's production defaults when loading files written before
            // these fields existed. Explicit rollback choices still round-trip.
            public bool? SimpleDdgiSampledAtlasEnabled { get; init; }
            public SimpleDdgiSampledAtlasCoverageMode? SimpleDdgiSampledAtlasCoverageMode { get; init; }
            public SimpleDdgiStoragePackingMode? SimpleDdgiStoragePackingMode { get; init; }
            public float SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold { get; init; } = 1.0f;
            public bool SimpleDdgiToroidalScrollingEnabled { get; init; } = true;
            public bool SimpleDdgiRegionalInvalidationEnabled { get; init; } = true;
            public bool SimpleDdgiMutationJournalEnabled { get; init; } = true;
            public bool SimpleDdgiMutationJournalValidationOracleEnabled { get; init; }
            public bool SimpleDdgiFogEnabled { get; init; } = true;
            public bool? SimpleDdgiDirectionalFogEnabled { get; init; }
            public bool? DdgiOpacityMicromapExperimentEnabled { get; init; }
            public bool? DdgiRayTracingPipelineExperimentEnabled { get; init; }
            public bool? SimpleDdgiDirectionalRayGuidingExperimentEnabled { get; init; }
            public bool? DdgiTaggedCausticCacheExperimentEnabled { get; init; }
            public bool? SimpleDdgiNearFieldResidualExperimentEnabled { get; init; }
            // Version-10 authoritative modes.  Nullable fields preserve the
            // schema-v9 boolean aliases when an older file is loaded.
            public SimpleDdgiReceiverFeedbackMode? SimpleDdgiReceiverFeedbackMode { get; init; }
            public DdgiOpacityMicromapMode? DdgiOpacityMicromapMode { get; init; }
            public SimpleDdgiDirectionalGuidingMode? SimpleDdgiDirectionalGuidingMode { get; init; }
            public GiCausticMode? GiCausticMode { get; init; }
            public SimpleDdgiNearFieldResidualMode? SimpleDdgiNearFieldResidualMode { get; init; }
            public SimpleDdgiNearFieldResidualQualityPreset?
                SimpleDdgiNearFieldResidualQualityPreset { get; init; }
            public bool? SimpleDdgiNearFieldResidualAdvancedOverridesEnabled { get; init; }
            public bool? SimpleDdgiNearFieldResidualLocalAdaptiveSchedulingEnabled
                { get; init; }
            public float? SimpleDdgiNearFieldResidualMaximumTraceDistanceMeters { get; init; }
            public int? SimpleDdgiNearFieldResidualRaysPerPixel { get; init; }
            public int? SimpleDdgiNearFieldResidualFilterIterationCount { get; init; }
            public float? SimpleDdgiNearFieldResidualIntensity { get; init; }
            public string? SimpleDdgiReceiverFeedbackQualificationId { get; init; }
            public string? DdgiOpacityMicromapQualificationId { get; init; }
            public string? SimpleDdgiDirectionalGuidingQualificationId { get; init; }
            public string? GiCausticQualificationId { get; init; }
            public string? SimpleDdgiNearFieldResidualQualificationId { get; init; }
            public bool SimpleDdgiParticlesEnabled { get; init; } = true;
            // Version-8 compatibility alias. Version 9 writes it for one schema
            // alongside the authoritative typed modes, then reads it only when
            // loading an older schema.
            public bool SimpleDdgiRoughSpecularEnabled { get; init; }
            public SimpleDdgiLocalLightSamplingMode? SimpleDdgiLocalLightSamplingMode { get; init; }
            public SimpleDdgiDirectionalRadianceMode? SimpleDdgiDirectionalRadianceMode { get; init; }
            public SimpleDdgiGlossyTransportMode? SimpleDdgiGlossyTransportMode { get; init; }
            public DdgiSkinnedGeometryMode? DdgiSkinnedGeometryMode { get; init; }
            public DdgiTransparentGeometryMode? DdgiTransparentGeometryMode { get; init; }
            public DdgiFoliageGeometryMode? DdgiFoliageGeometryMode { get; init; }
            public int? SimpleDdgiNearLocalLightSamplesPerHit { get; init; }
            public int? SimpleDdgiMidLocalLightSamplesPerHit { get; init; }
            public int? SimpleDdgiFarLocalLightSamplesPerHit { get; init; }
            public int? SimpleDdgiExactLocalLightThreshold { get; init; }
            public float? SimpleDdgiLightTreeUniformMixtureProbability { get; init; }
            public int? SimpleDdgiLightTreeMaximumRefitAge { get; init; }
            public ulong? DdgiDynamicBlasMemoryBudgetBytes { get; init; }
            public ulong? DdgiDynamicBlasScratchBudgetBytes { get; init; }
            public int? DdgiDynamicBlasBuildsPerFrame { get; init; }
            public int? DdgiDynamicBlasPrimitivesPerFrame { get; init; }
            public int? DdgiFoliageProxyTriangleBudget { get; init; }
            public int? DdgiFoliageProxyUpdateCadenceFrames { get; init; }
            public int? DdgiTransparencyCandidateLimit { get; init; }
            public int? DdgiTransparencyLayerLimit { get; init; }
            public int? DdgiDecalCandidateLimit { get; init; }
            public ulong? SimpleDdgiDirectionalRadianceMemoryBudgetBytes { get; init; }
            public float? SimpleDdgiRoughSpecularMinimumRoughness { get; init; }
            public float? SimpleDdgiRoughSpecularFullWeightRoughness { get; init; }
            public float SimpleDdgiProbeSpacing { get; init; } = 1.25f;
            public int SimpleDdgiRingCount { get; init; } = 3;
            public float SimpleDdgiRingBaseSpacing { get; init; } = 1.25f;
            public float SimpleDdgiRingSpacingMultiplier { get; init; } = 3.0f;
            public float SimpleDdgiViewForwardPlacementFraction { get; init; } = 0.6f;
            public SimpleDdgiVerticalRingPolicy SimpleDdgiVerticalRingPolicy { get; init; } = SimpleDdgiVerticalRingPolicy.CameraRelativeWithHysteresis;
            public float SimpleDdgiReceiverVerticalAnchor { get; init; } = 4.5f;
            public float SimpleDdgiVerticalRecenterHysteresisFraction { get; init; } = 0.25f;
            public int SimpleDdgiNearRingGridSizeX { get; init; } = 28;
            public int SimpleDdgiNearRingGridSizeY { get; init; } = 14;
            public int SimpleDdgiNearRingGridSizeZ { get; init; } = 28;
            public int SimpleDdgiMidRingGridSizeX { get; init; } = 18;
            public int SimpleDdgiMidRingGridSizeY { get; init; } = 10;
            public int SimpleDdgiMidRingGridSizeZ { get; init; } = 18;
            public int SimpleDdgiFarRingGridSizeX { get; init; } = 12;
            public int SimpleDdgiFarRingGridSizeY { get; init; } = 8;
            public int SimpleDdgiFarRingGridSizeZ { get; init; } = 12;
            public bool? SimpleDdgiRefinementBricksEnabled { get; init; }
            public int? SimpleDdgiRefinementMaximumBricks { get; init; }
            public int? SimpleDdgiRefinementGridSizeX { get; init; }
            public int? SimpleDdgiRefinementGridSizeY { get; init; }
            public int? SimpleDdgiRefinementGridSizeZ { get; init; }
            public float? SimpleDdgiRefinementSpacingScale { get; init; }
            public int? SimpleDdgiRefinementRetentionFrames { get; init; }
            public float? SimpleDdgiRefinementMinimumEmissiveLuminanceNits { get; init; }
            public float? SimpleDdgiRefinementMaximumEmitterAreaSquareMeters { get; init; }
            public bool? SimpleDdgiNearVisibilitySidecarEnabled { get; init; }
            public ulong? SimpleDdgiNearVisibilitySidecarMemoryBudgetBytes { get; init; }
            public int SimpleDdgiRaysPerProbe { get; init; } = 96;
            public int SimpleDdgiMaintenanceRaysPerProbe { get; init; } = 24;
            public int SimpleDdgiNearFullRaysPerProbe { get; init; } = 64;
            public int SimpleDdgiMidFullRaysPerProbe { get; init; } = 48;
            public int SimpleDdgiFarFullRaysPerProbe { get; init; } = 24;
            public int SimpleDdgiNearMaintenanceRaysPerProbe { get; init; } = 24;
            public int SimpleDdgiMidMaintenanceRaysPerProbe { get; init; } = 12;
            public int SimpleDdgiFarMaintenanceRaysPerProbe { get; init; } = 6;
            public int SimpleDdgiNearMinimumUpdateQuota { get; init; } = 512;
            public int SimpleDdgiMidMinimumUpdateQuota { get; init; } = 96;
            public int SimpleDdgiFarMinimumUpdateQuota { get; init; } = 24;
            public int SimpleDdgiNearMaximumUpdateQuota { get; init; } = 1_024;
            public int SimpleDdgiMidMaximumUpdateQuota { get; init; } = 324;
            public int SimpleDdgiFarMaximumUpdateQuota { get; init; } = 128;
            public int SimpleDdgiNearMaterialTextureMaxCascade { get; init; } = 1;
            public int SimpleDdgiMidMaterialTextureMaxCascade { get; init; } = 0;
            public int SimpleDdgiFarMaterialTextureMaxCascade { get; init; } = -1;
            public int SimpleDdgiNearMaxShadedLights { get; init; } = 8;
            public int SimpleDdgiMidMaxShadedLights { get; init; } = 4;
            public int SimpleDdgiFarMaxShadedLights { get; init; } = 2;
            public float SimpleDdgiHysteresis { get; init; } = 0.97f;
            public float SimpleDdgiHysteresisChangeThreshold { get; init; } = 0.50f;
            public float SimpleDdgiHysteresisStepThreshold { get; init; } = 0.80f;
            public int SimpleDdgiLightingDirtyFrameCount { get; init; } = 30;
            public int SimpleDdgiStableMaintenanceUpdateCount { get; init; } = 3;
            public float SimpleDdgiStableMaintenanceEmaThreshold { get; init; } = 0.03f;
            public float SimpleDdgiTransportSolverRelaxation { get; init; } = 0.70f;
            public float SimpleDdgiTransportAlbedoClamp { get; init; } = 0.95f;
            public float? SimpleDdgiTransportTailRelativeTolerance { get; init; }
            public int? SimpleDdgiTransportAcceleratedSweepCount { get; init; }
            public bool? SimpleDdgiTransportAccelerationEnabled { get; init; }
            public bool? SimpleDdgiTransportTailCertificationEnabled { get; init; }
            // These two names are deliberately ignored on write. Unknown legacy
            // fields are retained in ExtensionData and read by ApplyTo below so
            // old files migrate without keeping obsolete authority in new files.
            public int SimpleDdgiTransportSourceRefreshFrames { get; init; } = 2_048;
            public float SimpleDdgiAutomaticProbeDensityScale { get; init; } = 0.70f;
            // These are spacing-relative authoring controls used by the shared
            // forward gather.  Persist them with the layout so a capture or
            // reload cannot silently change interpolation/visibility behavior.
            public float SimpleDdgiNormalBias { get; init; } = 0.1f;
            public float SimpleDdgiViewBias { get; init; } = 0.3f;
            public float SimpleDdgiMaximumWorldBiasMeters { get; init; } = 0.20f;
            public float SimpleDdgiArchitecturalThicknessMeters { get; init; } = 0.80f;
            public int SimpleDdgiProbeUpdatesPerFrame { get; init; } = 2_048;
            public bool FarFieldClipmapEnabled { get; init; } = true;
            public bool FarFieldPagedEnabled { get; init; } = true;
            public bool GiFarFieldMaterialV2 { get; init; }
            public bool FarFieldSkyVisibilityEnabled { get; init; } = true;
            public bool FarFieldSunShadowEnabled { get; init; } = true;
            public int FarFieldClipmapResolution { get; init; } = 128;
            public float FarFieldStartDistance { get; init; } = 12.0f;
            public int FarFieldMaxTraceSteps { get; init; } = 256;
            public int FarFieldPageResolution { get; init; } = 32;
            public int FarFieldCascadeCount { get; init; } = 3;
            public int FarFieldResidentPageBudget { get; init; } = 48;
            public int FarFieldPageUpdatesPerFrame { get; init; } = 1;
            public int FarFieldPageRequestRadius { get; init; } = 1;
            public float FarFieldBaseVoxelSize { get; init; } = 1.0f;
            public float FarFieldCascadeVoxelScale { get; init; } = 3.0f;
            public ulong FarFieldMemoryBudgetBytes { get; init; } = 96UL * 1024UL * 1024UL;
            public bool FarFieldForceAll { get; init; }
            public bool StreamedGiAccelerationStructuresEnabled { get; init; } = true;
            public ulong GiAccelerationStructureMemoryBudgetBytes { get; init; } = 1024UL * 1024UL * 1024UL;
            public float GiAccelerationStructureStaticResidentDistance { get; init; } = 256.0f;
            public int GiAccelerationStructureMaximumStaticInstances { get; init; } = 8_192;
            public int GiAccelerationStructureEvictionGraceFrames { get; init; } = 120;
            public int DdgiProbeUpdatePrimaryRayBudget { get; init; } = GlobalIlluminationSettings.DefaultDdgiProbeUpdatePrimaryRayBudget;
            public int DdgiMaxShadedLights { get; init; } = 8;
            public int DdgiMaterialTextureMaxCascade { get; init; } = 1;
            public ulong DdgiAtlasMemoryBudgetBytes { get; init; } = GlobalIlluminationSettings.DefaultDdgiAtlasMemoryBudgetBytes;
            public float DdgiThinWallLeakClampStrength { get; init; } = 0.9f;
            public float DdgiSelfShadowBiasScale { get; init; } = 1.0f;
            public float ResolutionScale { get; init; } = 0.5f;
            public float MaxBounceDistance { get; init; } = 6.0f;
            public bool TemporalEnabled { get; init; } = true;
            public bool DenoiserEnabled { get; init; } = true;
            public float HistoryResponsiveness { get; init; } = 0.18f;
            public float NormalRejectionThreshold { get; init; } = 0.85f;
            public float DepthRejectionThreshold { get; init; } = 0.08f;
            public float LeakClampStrength { get; init; } = 0.75f;

            [JsonExtensionData]
            public Dictionary<string, JsonElement>? ExtensionData { get; init; }

            public static GlobalIlluminationFile FromSettings(GlobalIlluminationSettings settings)
            {
                return new GlobalIlluminationFile
                {
                    Enabled = settings.Enabled,
                    Mode = settings.Mode,
                    DebugView = settings.DebugView,
                    EmergencyGiFallbackEnabled = settings.EmergencyGiFallbackEnabled,
                    IndirectIntensity = settings.IndirectIntensity,
                    EnvironmentFallbackIntensity = settings.EnvironmentFallbackIntensity,
                    UseDdgi = settings.UseDdgi,
                    UseRayQueryBackend = settings.UseRayQueryBackend,
                    DdgiQualityTier = settings.DdgiQualityTier,
                    GiMaterialTransportV2 = settings.GiMaterialTransportV2,
                    GiEmissiveMeshSampling = settings.GiEmissiveMeshSampling,
                    DdgiProbeClassificationEnabled = settings.DdgiProbeClassificationEnabled,
                    DdgiProbeRelocationEnabled = settings.DdgiProbeRelocationEnabled,
                    DdgiProbeL1MetadataEnabled = settings.DdgiProbeL1MetadataEnabled,
                    DdgiCameraRelativeEnabled = settings.DdgiCameraRelativeEnabled,
                    DdgiAdaptiveBudgetingEnabled = settings.DdgiAdaptiveBudgetingEnabled,
                    DdgiThinWallPolicyEnabled = settings.DdgiThinWallPolicyEnabled,
                    DdgiAlphaMaskedTransportEnabled = settings.DdgiAlphaMaskedTransportEnabled,
                    SimpleDdgiSchedulerMode = settings.SimpleDdgiSchedulerMode.Sanitize(),
                    SimpleDdgiSchedulerReentryStableFrameCount =
                        settings.SimpleDdgiSchedulerReentryStableFrameCount,
                    SimpleDdgiProbeResidencyMode =
                        settings.SimpleDdgiProbeResidencyMode.Sanitize(),
                    SimpleDdgiSparsePhysicalPageBudget =
                        settings.SimpleDdgiSparsePhysicalPageBudget,
                    SimpleDdgiSparseMinimumPhysicalPageBudget =
                        settings.SimpleDdgiSparseMinimumPhysicalPageBudget,
                    SimpleDdgiSparseRetentionFrames =
                        settings.SimpleDdgiSparseRetentionFrames,
                    SimpleDdgiSparseMaximumAdmissionsPerFrame =
                        settings.SimpleDdgiSparseMaximumAdmissionsPerFrame,
                    SimpleDdgiSparseMaximumReceiverFeedbackRequests =
                        settings.SimpleDdgiSparseMaximumReceiverFeedbackRequests,
                    SimpleDdgiSparseInactiveRetryFrames =
                        settings.SimpleDdgiSparseInactiveRetryFrames,
                    SimpleDdgiAuthoredVolumes = settings.SimpleDdgiAuthoredVolumes
                        .Take(GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount)
                        .Select(SimpleDdgiAuthoredVolumeFile.FromSettings)
                        .ToArray(),
                    SimpleDdgiSharedMemoryBlendEnabled = settings.SimpleDdgiSharedMemoryBlendEnabled,
                    SimpleDdgiClassificationSchedulingEnabled = settings.SimpleDdgiClassificationSchedulingEnabled,
                    SimpleDdgiCostAwareSchedulingEnabled = settings.SimpleDdgiCostAwareSchedulingEnabled,
                    SimpleDdgiReceiverContributionFeedbackEnabled =
                        settings.SimpleDdgiReceiverContributionFeedbackEnabled,
                    SimpleDdgiPersistentWarmStartEnabled =
                        settings.SimpleDdgiPersistentWarmStartEnabled,
                    SimpleDdgiSparseResidualPropagationEnabled =
                        settings.SimpleDdgiSparseResidualPropagationEnabled,
                    SimpleDdgiUrgentRelightEnabled =
                        settings.SimpleDdgiUrgentRelightEnabled,
                    SimpleDdgiUrgentRelightProbeBudget =
                        settings.SimpleDdgiUrgentRelightProbeBudget,
                    SimpleDdgiSourceCacheLayoutMode =
                        settings.SimpleDdgiSourceCacheLayoutMode.Sanitize(),
                    SimpleDdgiClassificationReadbackEnabled = settings.SimpleDdgiClassificationReadbackEnabled,
                    SimpleDdgiAdaptiveHysteresisEnabled = settings.SimpleDdgiAdaptiveHysteresisEnabled,
                    SimpleDdgiLightingDirtyBoostEnabled = settings.SimpleDdgiLightingDirtyBoostEnabled,
                    SimpleDdgiDynamicGeometryDirtyBoostEnabled = settings.SimpleDdgiDynamicGeometryDirtyBoostEnabled,
                    SimpleDdgiAdaptiveRaysEnabled = settings.SimpleDdgiAdaptiveRaysEnabled,
                    SimpleDdgiTransportV2Enabled = settings.SimpleDdgiTransportV2Enabled,
                    SimpleDdgiThinSurfaceTransmissionEnabled = settings.SimpleDdgiThinSurfaceTransmissionEnabled,
                    SimpleDdgiAutomaticProbeDensityEnabled = settings.SimpleDdgiAutomaticProbeDensityEnabled,
                    SimpleDdgiStructuredGatherEnabled = settings.SimpleDdgiStructuredGatherEnabled,
                    SimpleDdgiReceiverCacheMode =
                        settings.SimpleDdgiReceiverCacheMode,
                    SimpleDdgiLayoutAdmissionMode = settings.SimpleDdgiLayoutAdmissionMode,
                    SimpleDdgiReducedBlendEnabled = settings.SimpleDdgiReducedBlendEnabled,
                    SimpleDdgiSampledAtlasEnabled = settings.SimpleDdgiSampledAtlasEnabled,
                    SimpleDdgiSampledAtlasCoverageMode = settings.SimpleDdgiSampledAtlasCoverageMode,
                    SimpleDdgiStoragePackingMode = settings.SimpleDdgiStoragePackingMode,
                    SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold = settings.SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold,
                    SimpleDdgiToroidalScrollingEnabled = settings.SimpleDdgiToroidalScrollingEnabled,
                    SimpleDdgiRegionalInvalidationEnabled = settings.SimpleDdgiRegionalInvalidationEnabled,
                    SimpleDdgiMutationJournalEnabled = settings.SimpleDdgiMutationJournalEnabled,
                    SimpleDdgiMutationJournalValidationOracleEnabled =
                        settings.SimpleDdgiMutationJournalValidationOracleEnabled,
                    SimpleDdgiFogEnabled = settings.SimpleDdgiFogEnabled,
                    SimpleDdgiDirectionalFogEnabled =
                        settings.SimpleDdgiDirectionalFogEnabled,
                    DdgiOpacityMicromapExperimentEnabled =
                        settings.DdgiOpacityMicromapExperimentEnabled,
                    DdgiRayTracingPipelineExperimentEnabled =
                        settings.DdgiRayTracingPipelineExperimentEnabled,
                    SimpleDdgiDirectionalRayGuidingExperimentEnabled =
                        settings.SimpleDdgiDirectionalRayGuidingExperimentEnabled,
                    DdgiTaggedCausticCacheExperimentEnabled =
                        settings.DdgiTaggedCausticCacheExperimentEnabled,
                    SimpleDdgiNearFieldResidualExperimentEnabled =
                        settings.SimpleDdgiNearFieldResidualExperimentEnabled,
                    SimpleDdgiReceiverFeedbackMode =
                        settings.SimpleDdgiReceiverFeedbackMode,
                    DdgiOpacityMicromapMode = settings.DdgiOpacityMicromapMode,
                    SimpleDdgiDirectionalGuidingMode =
                        settings.SimpleDdgiDirectionalGuidingMode,
                    GiCausticMode = settings.GiCausticMode,
                    SimpleDdgiNearFieldResidualMode =
                        settings.SimpleDdgiNearFieldResidualMode,
                    SimpleDdgiNearFieldResidualQualityPreset =
                        settings.SimpleDdgiNearFieldResidualQualityPreset,
                    SimpleDdgiNearFieldResidualAdvancedOverridesEnabled =
                        settings.SimpleDdgiNearFieldResidualAdvancedOverridesEnabled,
                    SimpleDdgiNearFieldResidualLocalAdaptiveSchedulingEnabled =
                        settings.SimpleDdgiNearFieldResidualLocalAdaptiveSchedulingEnabled,
                    SimpleDdgiNearFieldResidualMaximumTraceDistanceMeters =
                        settings.SimpleDdgiNearFieldResidualMaximumTraceDistanceMeters,
                    SimpleDdgiNearFieldResidualRaysPerPixel =
                        settings.SimpleDdgiNearFieldResidualRaysPerPixel,
                    SimpleDdgiNearFieldResidualFilterIterationCount =
                        settings.SimpleDdgiNearFieldResidualFilterIterationCount,
                    SimpleDdgiNearFieldResidualIntensity =
                        settings.SimpleDdgiNearFieldResidualIntensity,
                    SimpleDdgiReceiverFeedbackQualificationId =
                        settings.SimpleDdgiReceiverFeedbackQualificationId,
                    DdgiOpacityMicromapQualificationId =
                        settings.DdgiOpacityMicromapQualificationId,
                    SimpleDdgiDirectionalGuidingQualificationId =
                        settings.SimpleDdgiDirectionalGuidingQualificationId,
                    GiCausticQualificationId = settings.GiCausticQualificationId,
                    SimpleDdgiNearFieldResidualQualificationId =
                        settings.SimpleDdgiNearFieldResidualQualificationId,
                    SimpleDdgiParticlesEnabled = settings.SimpleDdgiParticlesEnabled,
                    SimpleDdgiRoughSpecularEnabled = settings.SimpleDdgiRoughSpecularEnabled,
                    SimpleDdgiLocalLightSamplingMode = settings.SimpleDdgiLocalLightSamplingMode,
                    SimpleDdgiDirectionalRadianceMode = settings.SimpleDdgiDirectionalRadianceMode,
                    SimpleDdgiGlossyTransportMode = settings.SimpleDdgiGlossyTransportMode,
                    DdgiSkinnedGeometryMode = settings.DdgiSkinnedGeometryMode,
                    DdgiTransparentGeometryMode = settings.DdgiTransparentGeometryMode,
                    DdgiFoliageGeometryMode = settings.DdgiFoliageGeometryMode,
                    SimpleDdgiNearLocalLightSamplesPerHit =
                        settings.SimpleDdgiNearLocalLightSamplesPerHit,
                    SimpleDdgiMidLocalLightSamplesPerHit =
                        settings.SimpleDdgiMidLocalLightSamplesPerHit,
                    SimpleDdgiFarLocalLightSamplesPerHit =
                        settings.SimpleDdgiFarLocalLightSamplesPerHit,
                    SimpleDdgiExactLocalLightThreshold = settings.SimpleDdgiExactLocalLightThreshold,
                    SimpleDdgiLightTreeUniformMixtureProbability =
                        settings.SimpleDdgiLightTreeUniformMixtureProbability,
                    SimpleDdgiLightTreeMaximumRefitAge = settings.SimpleDdgiLightTreeMaximumRefitAge,
                    DdgiDynamicBlasMemoryBudgetBytes = settings.DdgiDynamicBlasMemoryBudgetBytes,
                    DdgiDynamicBlasScratchBudgetBytes = settings.DdgiDynamicBlasScratchBudgetBytes,
                    DdgiDynamicBlasBuildsPerFrame = settings.DdgiDynamicBlasBuildsPerFrame,
                    DdgiDynamicBlasPrimitivesPerFrame = settings.DdgiDynamicBlasPrimitivesPerFrame,
                    DdgiFoliageProxyTriangleBudget = settings.DdgiFoliageProxyTriangleBudget,
                    DdgiFoliageProxyUpdateCadenceFrames = settings.DdgiFoliageProxyUpdateCadenceFrames,
                    DdgiTransparencyCandidateLimit = settings.DdgiTransparencyCandidateLimit,
                    DdgiTransparencyLayerLimit = settings.DdgiTransparencyLayerLimit,
                    DdgiDecalCandidateLimit = settings.DdgiDecalCandidateLimit,
                    SimpleDdgiDirectionalRadianceMemoryBudgetBytes =
                        settings.SimpleDdgiDirectionalRadianceMemoryBudgetBytes,
                    SimpleDdgiRoughSpecularMinimumRoughness =
                        settings.SimpleDdgiRoughSpecularMinimumRoughness,
                    SimpleDdgiRoughSpecularFullWeightRoughness =
                        settings.SimpleDdgiRoughSpecularFullWeightRoughness,
                    SimpleDdgiProbeSpacing = settings.SimpleDdgiProbeSpacing,
                    SimpleDdgiRingCount = settings.SimpleDdgiRingCount,
                    SimpleDdgiRingBaseSpacing = settings.SimpleDdgiRingBaseSpacing,
                    SimpleDdgiRingSpacingMultiplier = settings.SimpleDdgiRingSpacingMultiplier,
                    SimpleDdgiViewForwardPlacementFraction = settings.SimpleDdgiViewForwardPlacementFraction,
                    SimpleDdgiVerticalRingPolicy = settings.SimpleDdgiVerticalRingPolicy,
                    SimpleDdgiReceiverVerticalAnchor = settings.SimpleDdgiReceiverVerticalAnchor,
                    SimpleDdgiVerticalRecenterHysteresisFraction = settings.SimpleDdgiVerticalRecenterHysteresisFraction,
                    SimpleDdgiNearRingGridSizeX = settings.SimpleDdgiNearRingGridSizeX,
                    SimpleDdgiNearRingGridSizeY = settings.SimpleDdgiNearRingGridSizeY,
                    SimpleDdgiNearRingGridSizeZ = settings.SimpleDdgiNearRingGridSizeZ,
                    SimpleDdgiMidRingGridSizeX = settings.SimpleDdgiMidRingGridSizeX,
                    SimpleDdgiMidRingGridSizeY = settings.SimpleDdgiMidRingGridSizeY,
                    SimpleDdgiMidRingGridSizeZ = settings.SimpleDdgiMidRingGridSizeZ,
                    SimpleDdgiFarRingGridSizeX = settings.SimpleDdgiFarRingGridSizeX,
                    SimpleDdgiFarRingGridSizeY = settings.SimpleDdgiFarRingGridSizeY,
                    SimpleDdgiFarRingGridSizeZ = settings.SimpleDdgiFarRingGridSizeZ,
                    SimpleDdgiRefinementBricksEnabled = settings.SimpleDdgiRefinementBricksEnabled,
                    SimpleDdgiRefinementMaximumBricks = settings.SimpleDdgiRefinementMaximumBricks,
                    SimpleDdgiRefinementGridSizeX = settings.SimpleDdgiRefinementGridSizeX,
                    SimpleDdgiRefinementGridSizeY = settings.SimpleDdgiRefinementGridSizeY,
                    SimpleDdgiRefinementGridSizeZ = settings.SimpleDdgiRefinementGridSizeZ,
                    SimpleDdgiRefinementSpacingScale = settings.SimpleDdgiRefinementSpacingScale,
                    SimpleDdgiRefinementRetentionFrames = settings.SimpleDdgiRefinementRetentionFrames,
                    SimpleDdgiRefinementMinimumEmissiveLuminanceNits =
                        settings.SimpleDdgiRefinementMinimumEmissiveLuminanceNits,
                    SimpleDdgiRefinementMaximumEmitterAreaSquareMeters =
                        settings.SimpleDdgiRefinementMaximumEmitterAreaSquareMeters,
                    SimpleDdgiNearVisibilitySidecarEnabled =
                        settings.SimpleDdgiNearVisibilitySidecarEnabled,
                    SimpleDdgiNearVisibilitySidecarMemoryBudgetBytes =
                        settings.SimpleDdgiNearVisibilitySidecarMemoryBudgetBytes,
                    SimpleDdgiRaysPerProbe = settings.SimpleDdgiRaysPerProbe,
                    SimpleDdgiMaintenanceRaysPerProbe = settings.SimpleDdgiMaintenanceRaysPerProbe,
                    SimpleDdgiNearFullRaysPerProbe = settings.SimpleDdgiNearFullRaysPerProbe,
                    SimpleDdgiMidFullRaysPerProbe = settings.SimpleDdgiMidFullRaysPerProbe,
                    SimpleDdgiFarFullRaysPerProbe = settings.SimpleDdgiFarFullRaysPerProbe,
                    SimpleDdgiNearMaintenanceRaysPerProbe = settings.SimpleDdgiNearMaintenanceRaysPerProbe,
                    SimpleDdgiMidMaintenanceRaysPerProbe = settings.SimpleDdgiMidMaintenanceRaysPerProbe,
                    SimpleDdgiFarMaintenanceRaysPerProbe = settings.SimpleDdgiFarMaintenanceRaysPerProbe,
                    SimpleDdgiNearMinimumUpdateQuota = settings.SimpleDdgiNearMinimumUpdateQuota,
                    SimpleDdgiMidMinimumUpdateQuota = settings.SimpleDdgiMidMinimumUpdateQuota,
                    SimpleDdgiFarMinimumUpdateQuota = settings.SimpleDdgiFarMinimumUpdateQuota,
                    SimpleDdgiNearMaximumUpdateQuota = settings.SimpleDdgiNearMaximumUpdateQuota,
                    SimpleDdgiMidMaximumUpdateQuota = settings.SimpleDdgiMidMaximumUpdateQuota,
                    SimpleDdgiFarMaximumUpdateQuota = settings.SimpleDdgiFarMaximumUpdateQuota,
                    SimpleDdgiNearMaterialTextureMaxCascade = settings.SimpleDdgiNearMaterialTextureMaxCascade,
                    SimpleDdgiMidMaterialTextureMaxCascade = settings.SimpleDdgiMidMaterialTextureMaxCascade,
                    SimpleDdgiFarMaterialTextureMaxCascade = settings.SimpleDdgiFarMaterialTextureMaxCascade,
                    SimpleDdgiNearMaxShadedLights = settings.SimpleDdgiNearMaxShadedLights,
                    SimpleDdgiMidMaxShadedLights = settings.SimpleDdgiMidMaxShadedLights,
                    SimpleDdgiFarMaxShadedLights = settings.SimpleDdgiFarMaxShadedLights,
                    SimpleDdgiHysteresis = settings.SimpleDdgiHysteresis,
                    SimpleDdgiHysteresisChangeThreshold = settings.SimpleDdgiHysteresisChangeThreshold,
                    SimpleDdgiHysteresisStepThreshold = settings.SimpleDdgiHysteresisStepThreshold,
                    SimpleDdgiLightingDirtyFrameCount = settings.SimpleDdgiLightingDirtyFrameCount,
                    SimpleDdgiStableMaintenanceUpdateCount = settings.SimpleDdgiStableMaintenanceUpdateCount,
                    SimpleDdgiStableMaintenanceEmaThreshold = settings.SimpleDdgiStableMaintenanceEmaThreshold,
                    SimpleDdgiTransportSolverRelaxation = settings.SimpleDdgiTransportSolverRelaxation,
                    SimpleDdgiTransportAlbedoClamp = settings.SimpleDdgiTransportAlbedoClamp,
                    SimpleDdgiTransportTailRelativeTolerance = settings.SimpleDdgiTransportTailRelativeTolerance,
                    SimpleDdgiTransportAcceleratedSweepCount = settings.SimpleDdgiTransportAcceleratedSweepCount,
                    SimpleDdgiTransportAccelerationEnabled = settings.SimpleDdgiTransportAccelerationEnabled,
                    SimpleDdgiTransportTailCertificationEnabled = settings.SimpleDdgiTransportTailCertificationEnabled,
                    SimpleDdgiTransportSourceRefreshFrames = settings.SimpleDdgiTransportSourceRefreshFrames,
                    SimpleDdgiAutomaticProbeDensityScale = settings.SimpleDdgiAutomaticProbeDensityScale,
                    SimpleDdgiNormalBias = settings.SimpleDdgiNormalBias,
                    SimpleDdgiViewBias = settings.SimpleDdgiViewBias,
                    SimpleDdgiMaximumWorldBiasMeters = settings.SimpleDdgiMaximumWorldBiasMeters,
                    SimpleDdgiArchitecturalThicknessMeters = settings.SimpleDdgiArchitecturalThicknessMeters,
                    SimpleDdgiProbeUpdatesPerFrame = settings.SimpleDdgiProbeUpdatesPerFrame,
                    FarFieldClipmapEnabled = settings.FarFieldClipmapEnabled,
                    FarFieldPagedEnabled = settings.FarFieldPagedEnabled,
                    GiFarFieldMaterialV2 = settings.GiFarFieldMaterialV2,
                    FarFieldSkyVisibilityEnabled = settings.FarFieldSkyVisibilityEnabled,
                    FarFieldSunShadowEnabled = settings.FarFieldSunShadowEnabled,
                    FarFieldClipmapResolution = settings.FarFieldClipmapResolution,
                    FarFieldStartDistance = settings.FarFieldStartDistance,
                    FarFieldMaxTraceSteps = settings.FarFieldMaxTraceSteps,
                    FarFieldPageResolution = settings.FarFieldPageResolution,
                    FarFieldCascadeCount = settings.FarFieldCascadeCount,
                    FarFieldResidentPageBudget = settings.FarFieldResidentPageBudget,
                    FarFieldPageUpdatesPerFrame = settings.FarFieldPageUpdatesPerFrame,
                    FarFieldPageRequestRadius = settings.FarFieldPageRequestRadius,
                    FarFieldBaseVoxelSize = settings.FarFieldBaseVoxelSize,
                    FarFieldCascadeVoxelScale = settings.FarFieldCascadeVoxelScale,
                    FarFieldMemoryBudgetBytes = settings.FarFieldMemoryBudgetBytes,
                    FarFieldForceAll = settings.FarFieldForceAll,
                    StreamedGiAccelerationStructuresEnabled = settings.StreamedGiAccelerationStructuresEnabled,
                    GiAccelerationStructureMemoryBudgetBytes = settings.GiAccelerationStructureMemoryBudgetBytes,
                    GiAccelerationStructureStaticResidentDistance = settings.GiAccelerationStructureStaticResidentDistance,
                    GiAccelerationStructureMaximumStaticInstances = settings.GiAccelerationStructureMaximumStaticInstances,
                    GiAccelerationStructureEvictionGraceFrames = settings.GiAccelerationStructureEvictionGraceFrames,
                    DdgiAsyncComputeEnabled = settings.DdgiAsyncComputeEnabled,
                    DdgiProbeUpdatePrimaryRayBudget = settings.DdgiProbeUpdatePrimaryRayBudget,
                    DdgiMaxShadedLights = settings.DdgiMaxShadedLights,
                    DdgiMaterialTextureMaxCascade = settings.DdgiMaterialTextureMaxCascade,
                    DdgiAtlasMemoryBudgetBytes = settings.DdgiAtlasMemoryBudgetBytes,
                    DdgiThinWallLeakClampStrength = settings.DdgiThinWallLeakClampStrength,
                    DdgiSelfShadowBiasScale = settings.DdgiSelfShadowBiasScale,
                    ResolutionScale = settings.ResolutionScale,
                    MaxBounceDistance = settings.MaxBounceDistance,
                    TemporalEnabled = settings.TemporalEnabled,
                    DenoiserEnabled = settings.DenoiserEnabled,
                    HistoryResponsiveness = settings.HistoryResponsiveness,
                    NormalRejectionThreshold = settings.NormalRejectionThreshold,
                    DepthRejectionThreshold = settings.DepthRejectionThreshold,
                    LeakClampStrength = settings.LeakClampStrength
                };
            }

            public void ApplyTo(GlobalIlluminationSettings settings, int sourceVersion)
            {
                settings.Enabled = Enabled;
                settings.Mode = Mode;
                settings.DebugView = DebugView;
                settings.EmergencyGiFallbackEnabled = EmergencyGiFallbackEnabled;
                settings.IndirectIntensity = IndirectIntensity;
                settings.EnvironmentFallbackIntensity = EnvironmentFallbackIntensity;
                settings.UseDdgi = UseDdgi;
                if (UseRayQueryBackend.HasValue)
                    settings.UseRayQueryBackend = UseRayQueryBackend.Value;
                settings.DdgiQualityTier = DdgiQualityTier;
                settings.GiMaterialTransportV2 = GiMaterialTransportV2;
                settings.GiEmissiveMeshSampling = GiEmissiveMeshSampling;
                settings.DdgiProbeClassificationEnabled = DdgiProbeClassificationEnabled;
                settings.DdgiProbeRelocationEnabled = DdgiProbeRelocationEnabled;
                settings.DdgiProbeL1MetadataEnabled = DdgiProbeL1MetadataEnabled;
                settings.DdgiCameraRelativeEnabled = DdgiCameraRelativeEnabled;
                settings.DdgiAdaptiveBudgetingEnabled = DdgiAdaptiveBudgetingEnabled;
                settings.DdgiThinWallPolicyEnabled = DdgiThinWallPolicyEnabled;
                settings.DdgiAlphaMaskedTransportEnabled = DdgiAlphaMaskedTransportEnabled;
                if (SimpleDdgiSchedulerMode.HasValue)
                {
                    settings.SimpleDdgiSchedulerMode =
                        SimpleDdgiSchedulerMode.Value.Sanitize();
                }
                if (SimpleDdgiSchedulerReentryStableFrameCount.HasValue)
                {
                    settings.SimpleDdgiSchedulerReentryStableFrameCount =
                        SimpleDdgiSchedulerReentryStableFrameCount.Value;
                }
                if (SimpleDdgiProbeResidencyMode.HasValue)
                {
                    settings.SimpleDdgiProbeResidencyMode =
                        SimpleDdgiProbeResidencyMode.Value.Sanitize();
                }
                if (SimpleDdgiSparsePhysicalPageBudget.HasValue)
                {
                    settings.SimpleDdgiSparsePhysicalPageBudget =
                        SimpleDdgiSparsePhysicalPageBudget.Value;
                }
                if (SimpleDdgiSparseMinimumPhysicalPageBudget.HasValue)
                {
                    settings.SimpleDdgiSparseMinimumPhysicalPageBudget =
                        SimpleDdgiSparseMinimumPhysicalPageBudget.Value;
                }
                if (SimpleDdgiSparseRetentionFrames.HasValue)
                    settings.SimpleDdgiSparseRetentionFrames = SimpleDdgiSparseRetentionFrames.Value;
                if (SimpleDdgiSparseMaximumAdmissionsPerFrame.HasValue)
                {
                    settings.SimpleDdgiSparseMaximumAdmissionsPerFrame =
                        SimpleDdgiSparseMaximumAdmissionsPerFrame.Value;
                }
                if (SimpleDdgiSparseMaximumReceiverFeedbackRequests.HasValue)
                {
                    settings.SimpleDdgiSparseMaximumReceiverFeedbackRequests =
                        SimpleDdgiSparseMaximumReceiverFeedbackRequests.Value;
                }
                if (SimpleDdgiSparseInactiveRetryFrames.HasValue)
                    settings.SimpleDdgiSparseInactiveRetryFrames = SimpleDdgiSparseInactiveRetryFrames.Value;
                settings.SimpleDdgiAuthoredVolumes.Clear();
                foreach (SimpleDdgiAuthoredVolumeFile? authoredVolume in
                    (SimpleDdgiAuthoredVolumes ?? Array.Empty<SimpleDdgiAuthoredVolumeFile>())
                    .Take(GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount))
                {
                    if (authoredVolume != null)
                        settings.SimpleDdgiAuthoredVolumes.Add(authoredVolume.ToSettings());
                }
                settings.SimpleDdgiSharedMemoryBlendEnabled = SimpleDdgiSharedMemoryBlendEnabled;
                settings.SimpleDdgiClassificationSchedulingEnabled = SimpleDdgiClassificationSchedulingEnabled;
                settings.SimpleDdgiCostAwareSchedulingEnabled = SimpleDdgiCostAwareSchedulingEnabled;
                settings.SimpleDdgiReceiverContributionFeedbackEnabled =
                    SimpleDdgiReceiverContributionFeedbackEnabled;
                settings.SimpleDdgiPersistentWarmStartEnabled =
                    SimpleDdgiPersistentWarmStartEnabled;
                settings.SimpleDdgiSparseResidualPropagationEnabled =
                    SimpleDdgiSparseResidualPropagationEnabled;
                settings.SimpleDdgiUrgentRelightEnabled =
                    SimpleDdgiUrgentRelightEnabled;
                if (SimpleDdgiUrgentRelightProbeBudget.HasValue)
                {
                    settings.SimpleDdgiUrgentRelightProbeBudget =
                        SimpleDdgiUrgentRelightProbeBudget.Value;
                }
                settings.SimpleDdgiSourceCacheLayoutMode =
                    SimpleDdgiSourceCacheLayoutMode.Sanitize();
                settings.SimpleDdgiClassificationReadbackEnabled = SimpleDdgiClassificationReadbackEnabled;
                settings.SimpleDdgiAdaptiveHysteresisEnabled = SimpleDdgiAdaptiveHysteresisEnabled;
                settings.SimpleDdgiLightingDirtyBoostEnabled = SimpleDdgiLightingDirtyBoostEnabled;
                settings.SimpleDdgiDynamicGeometryDirtyBoostEnabled = SimpleDdgiDynamicGeometryDirtyBoostEnabled;
                settings.SimpleDdgiAdaptiveRaysEnabled = SimpleDdgiAdaptiveRaysEnabled;
                settings.SimpleDdgiTransportV2Enabled = SimpleDdgiTransportV2Enabled;
                settings.SimpleDdgiThinSurfaceTransmissionEnabled = SimpleDdgiThinSurfaceTransmissionEnabled;
                settings.SimpleDdgiAutomaticProbeDensityEnabled = SimpleDdgiAutomaticProbeDensityEnabled;
                settings.SimpleDdgiStructuredGatherEnabled = SimpleDdgiStructuredGatherEnabled;
                if (SimpleDdgiReceiverCacheMode.HasValue &&
                    (sourceVersion >= 22 ||
                     SimpleDdgiReceiverCacheMode.Value.Sanitize() !=
                     global::Njulf.Rendering.Data.SimpleDdgiReceiverCacheMode.Exact))
                {
                    settings.SimpleDdgiReceiverCacheMode =
                        SimpleDdgiReceiverCacheMode.Value.Sanitize();
                }
                settings.SimpleDdgiLayoutAdmissionMode = SimpleDdgiLayoutAdmissionMode;
                settings.SimpleDdgiReducedBlendEnabled = SimpleDdgiReducedBlendEnabled;
                if (SimpleDdgiSampledAtlasEnabled.HasValue)
                {
                    settings.SimpleDdgiSampledAtlasEnabled =
                        SimpleDdgiSampledAtlasEnabled.Value;
                }
                if (SimpleDdgiSampledAtlasCoverageMode.HasValue)
                {
                    settings.SimpleDdgiSampledAtlasCoverageMode =
                        SimpleDdgiSampledAtlasCoverageMode.Value.Sanitize();
                }
                if (SimpleDdgiStoragePackingMode.HasValue)
                {
                    settings.SimpleDdgiStoragePackingMode =
                        SimpleDdgiStoragePackingMode.Value.Sanitize();
                }
                settings.SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold = SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold;
                settings.SimpleDdgiToroidalScrollingEnabled = SimpleDdgiToroidalScrollingEnabled;
                settings.SimpleDdgiRegionalInvalidationEnabled = SimpleDdgiRegionalInvalidationEnabled;
                settings.SimpleDdgiMutationJournalEnabled = SimpleDdgiMutationJournalEnabled;
                settings.SimpleDdgiMutationJournalValidationOracleEnabled =
                    SimpleDdgiMutationJournalValidationOracleEnabled;
                settings.SimpleDdgiFogEnabled = SimpleDdgiFogEnabled;
                if (SimpleDdgiDirectionalFogEnabled.HasValue)
                {
                    settings.SimpleDdgiDirectionalFogEnabled =
                        SimpleDdgiDirectionalFogEnabled.Value;
                }
                if (DdgiOpacityMicromapExperimentEnabled.HasValue)
                {
                    settings.DdgiOpacityMicromapExperimentEnabled =
                        DdgiOpacityMicromapExperimentEnabled.Value;
                }
                if (DdgiRayTracingPipelineExperimentEnabled.HasValue)
                {
                    settings.DdgiRayTracingPipelineExperimentEnabled =
                        DdgiRayTracingPipelineExperimentEnabled.Value;
                }
                if (SimpleDdgiDirectionalRayGuidingExperimentEnabled.HasValue)
                {
                    settings.SimpleDdgiDirectionalRayGuidingExperimentEnabled =
                        SimpleDdgiDirectionalRayGuidingExperimentEnabled.Value;
                }
                if (DdgiTaggedCausticCacheExperimentEnabled.HasValue)
                {
                    settings.DdgiTaggedCausticCacheExperimentEnabled =
                        DdgiTaggedCausticCacheExperimentEnabled.Value;
                }
                if (SimpleDdgiNearFieldResidualExperimentEnabled.HasValue)
                {
                    settings.SimpleDdgiNearFieldResidualExperimentEnabled =
                        SimpleDdgiNearFieldResidualExperimentEnabled.Value;
                }
                // New modes override their legacy aliases when present.  This
                // preserves an authored Off/AutoQualified request even though
                // schema-v10 also writes the old booleans for one-schema read
                // compatibility.
                if (SimpleDdgiReceiverFeedbackMode.HasValue)
                {
                    settings.SimpleDdgiReceiverFeedbackMode =
                        SimpleDdgiReceiverFeedbackMode.Value;
                }
                if (DdgiOpacityMicromapMode.HasValue)
                {
                    settings.DdgiOpacityMicromapMode = DdgiOpacityMicromapMode.Value;
                }
                if (SimpleDdgiDirectionalGuidingMode.HasValue)
                {
                    settings.SimpleDdgiDirectionalGuidingMode =
                        SimpleDdgiDirectionalGuidingMode.Value;
                }
                if (GiCausticMode.HasValue)
                    settings.GiCausticMode = GiCausticMode.Value;
                if (SimpleDdgiNearFieldResidualMode.HasValue)
                {
                    settings.SimpleDdgiNearFieldResidualMode =
                        SimpleDdgiNearFieldResidualMode.Value;
                }
                // AutoQualified was the schema-v14 rollout default, but without
                // installed device evidence it resolved to Off. Schema v15
                // intentionally upgrades that prior default to the bounded
                // explicit V2 path. Current-schema AutoQualified remains a
                // durable authored choice, and every other mode is preserved.
                if (sourceVersion < 15 &&
                    settings.SimpleDdgiNearFieldResidualMode ==
                        global::Njulf.Rendering.Resources
                            .SimpleDdgiNearFieldResidualMode.AutoQualified)
                {
                    settings.SimpleDdgiNearFieldResidualMode =
                        global::Njulf.Rendering.Resources
                            .SimpleDdgiNearFieldResidualMode.HiZAdaptive;
                }
                settings.SimpleDdgiNearFieldResidualQualityPreset =
                    SimpleDdgiNearFieldResidualQualityPreset ??
                    global::Njulf.Rendering.Resources
                        .SimpleDdgiNearFieldResidualQualityPreset.Balanced;
                if (SimpleDdgiNearFieldResidualAdvancedOverridesEnabled.HasValue)
                {
                    settings.SimpleDdgiNearFieldResidualAdvancedOverridesEnabled =
                        SimpleDdgiNearFieldResidualAdvancedOverridesEnabled.Value;
                }
                if (SimpleDdgiNearFieldResidualLocalAdaptiveSchedulingEnabled.HasValue &&
                    (sourceVersion >= 22 ||
                     SimpleDdgiNearFieldResidualLocalAdaptiveSchedulingEnabled.Value))
                {
                    settings.SimpleDdgiNearFieldResidualLocalAdaptiveSchedulingEnabled =
                        SimpleDdgiNearFieldResidualLocalAdaptiveSchedulingEnabled.Value;
                }
                if (SimpleDdgiNearFieldResidualMaximumTraceDistanceMeters.HasValue)
                {
                    settings.SimpleDdgiNearFieldResidualMaximumTraceDistanceMeters =
                        SimpleDdgiNearFieldResidualMaximumTraceDistanceMeters.Value;
                }
                if (SimpleDdgiNearFieldResidualRaysPerPixel.HasValue)
                {
                    settings.SimpleDdgiNearFieldResidualRaysPerPixel =
                        SimpleDdgiNearFieldResidualRaysPerPixel.Value;
                }
                if (SimpleDdgiNearFieldResidualFilterIterationCount.HasValue)
                {
                    settings.SimpleDdgiNearFieldResidualFilterIterationCount =
                        SimpleDdgiNearFieldResidualFilterIterationCount.Value;
                }
                if (SimpleDdgiNearFieldResidualIntensity.HasValue)
                {
                    settings.SimpleDdgiNearFieldResidualIntensity =
                        SimpleDdgiNearFieldResidualIntensity.Value;
                }
                if (SimpleDdgiReceiverFeedbackQualificationId != null)
                {
                    settings.SimpleDdgiReceiverFeedbackQualificationId =
                        SimpleDdgiReceiverFeedbackQualificationId;
                }
                if (DdgiOpacityMicromapQualificationId != null)
                {
                    settings.DdgiOpacityMicromapQualificationId =
                        DdgiOpacityMicromapQualificationId;
                }
                if (SimpleDdgiDirectionalGuidingQualificationId != null)
                {
                    settings.SimpleDdgiDirectionalGuidingQualificationId =
                        SimpleDdgiDirectionalGuidingQualificationId;
                }
                if (GiCausticQualificationId != null)
                    settings.GiCausticQualificationId = GiCausticQualificationId;
                // GPU/evidence/source semantics all changed for v14. Preserve
                // authored mode intent, but never carry a pre-v14 credential
                // into the V13/V6 admission path.
                if (sourceVersion >= 14 &&
                    SimpleDdgiNearFieldResidualQualificationId != null)
                {
                    settings.SimpleDdgiNearFieldResidualQualificationId =
                        SimpleDdgiNearFieldResidualQualificationId;
                }
                else if (sourceVersion < 14)
                {
                    settings.SimpleDdgiNearFieldResidualQualificationId = string.Empty;
                }
                settings.SimpleDdgiParticlesEnabled = SimpleDdgiParticlesEnabled;
                if (sourceVersion < 9)
                {
                    settings.SimpleDdgiLocalLightSamplingMode =
                        global::Njulf.Rendering.Data.SimpleDdgiLocalLightSamplingMode.LegacyTopKReference;
                    settings.SimpleDdgiNearLocalLightSamplesPerHit =
                        SimpleDdgiNearMaxShadedLights;
                    settings.SimpleDdgiMidLocalLightSamplesPerHit =
                        SimpleDdgiMidMaxShadedLights;
                    settings.SimpleDdgiFarLocalLightSamplesPerHit =
                        SimpleDdgiFarMaxShadedLights;
                    settings.SimpleDdgiExactLocalLightThreshold = 0;
                    settings.DdgiSkinnedGeometryMode =
                        global::Njulf.Rendering.Data.DdgiSkinnedGeometryMode.ConservativeProxy;
                    settings.DdgiTransparentGeometryMode =
                        global::Njulf.Rendering.Data.DdgiTransparentGeometryMode.MaskAndThin;
                    settings.DdgiFoliageGeometryMode =
                        global::Njulf.Rendering.Data.DdgiFoliageGeometryMode.Excluded;
                    settings.SimpleDdgiDirectionalRadianceMode =
                        SimpleDdgiRoughSpecularEnabled
                            ? global::Njulf.Rendering.Data.SimpleDdgiDirectionalRadianceMode.L2
                            : global::Njulf.Rendering.Data.SimpleDdgiDirectionalRadianceMode.Off;
                    settings.SimpleDdgiGlossyTransportMode =
                        SimpleDdgiRoughSpecularEnabled
                            ? global::Njulf.Rendering.Data.SimpleDdgiGlossyTransportMode.ReceiverOnly
                            : global::Njulf.Rendering.Data.SimpleDdgiGlossyTransportMode.Off;
                    settings.ContentDependentSettingsMigrationDiagnostic =
                        SimpleDdgiRoughSpecularEnabled
                            ? "Schema v8 rough-specular opt-in migrated explicitly to L2/ReceiverOnly; device qualification is still required."
                            : "Schema v8 DDGI content additions retained the qualified legacy/reference behavior and directional radiance remains Off.";
                }
                else
                {
                    if (SimpleDdgiLocalLightSamplingMode.HasValue)
                        settings.SimpleDdgiLocalLightSamplingMode = SimpleDdgiLocalLightSamplingMode.Value;
                    if (SimpleDdgiDirectionalRadianceMode.HasValue)
                        settings.SimpleDdgiDirectionalRadianceMode = SimpleDdgiDirectionalRadianceMode.Value;
                    if (SimpleDdgiGlossyTransportMode.HasValue)
                    {
                        SimpleDdgiGlossyTransportMode requestedGlossy =
                            SimpleDdgiGlossyTransportMode.Value;
                        if (sourceVersion < 12 && requestedGlossy ==
                            global::Njulf.Rendering.Data
                                .SimpleDdgiGlossyTransportMode.RecursiveCertified)
                        {
                            requestedGlossy = global::Njulf.Rendering.Data
                                .SimpleDdgiGlossyTransportMode.OneBounce;
                            settings.ContentDependentSettingsMigrationDiagnostic =
                                "Legacy RecursiveExperimental transport was not certified; migrated to OneBounce.";
                        }
                        settings.SimpleDdgiGlossyTransportMode = requestedGlossy;
                    }
                    if (DdgiSkinnedGeometryMode.HasValue)
                        settings.DdgiSkinnedGeometryMode = DdgiSkinnedGeometryMode.Value;
                    if (DdgiTransparentGeometryMode.HasValue)
                        settings.DdgiTransparentGeometryMode = DdgiTransparentGeometryMode.Value;
                    if (DdgiFoliageGeometryMode.HasValue)
                        settings.DdgiFoliageGeometryMode = DdgiFoliageGeometryMode.Value;
                    if (SimpleDdgiNearLocalLightSamplesPerHit.HasValue)
                    {
                        settings.SimpleDdgiNearLocalLightSamplesPerHit =
                            SimpleDdgiNearLocalLightSamplesPerHit.Value;
                    }
                    if (SimpleDdgiMidLocalLightSamplesPerHit.HasValue)
                    {
                        settings.SimpleDdgiMidLocalLightSamplesPerHit =
                            SimpleDdgiMidLocalLightSamplesPerHit.Value;
                    }
                    if (SimpleDdgiFarLocalLightSamplesPerHit.HasValue)
                    {
                        settings.SimpleDdgiFarLocalLightSamplesPerHit =
                            SimpleDdgiFarLocalLightSamplesPerHit.Value;
                    }
                    if (SimpleDdgiExactLocalLightThreshold.HasValue)
                        settings.SimpleDdgiExactLocalLightThreshold = SimpleDdgiExactLocalLightThreshold.Value;
                    if (SimpleDdgiLightTreeUniformMixtureProbability.HasValue)
                    {
                        settings.SimpleDdgiLightTreeUniformMixtureProbability =
                            SimpleDdgiLightTreeUniformMixtureProbability.Value;
                    }
                    if (SimpleDdgiLightTreeMaximumRefitAge.HasValue)
                        settings.SimpleDdgiLightTreeMaximumRefitAge = SimpleDdgiLightTreeMaximumRefitAge.Value;
                    if (DdgiDynamicBlasMemoryBudgetBytes.HasValue)
                        settings.DdgiDynamicBlasMemoryBudgetBytes = DdgiDynamicBlasMemoryBudgetBytes.Value;
                    if (DdgiDynamicBlasScratchBudgetBytes.HasValue)
                        settings.DdgiDynamicBlasScratchBudgetBytes = DdgiDynamicBlasScratchBudgetBytes.Value;
                    if (DdgiDynamicBlasBuildsPerFrame.HasValue)
                        settings.DdgiDynamicBlasBuildsPerFrame = DdgiDynamicBlasBuildsPerFrame.Value;
                    if (DdgiDynamicBlasPrimitivesPerFrame.HasValue)
                        settings.DdgiDynamicBlasPrimitivesPerFrame = DdgiDynamicBlasPrimitivesPerFrame.Value;
                    if (DdgiFoliageProxyTriangleBudget.HasValue)
                        settings.DdgiFoliageProxyTriangleBudget = DdgiFoliageProxyTriangleBudget.Value;
                    if (DdgiFoliageProxyUpdateCadenceFrames.HasValue)
                    {
                        settings.DdgiFoliageProxyUpdateCadenceFrames =
                            DdgiFoliageProxyUpdateCadenceFrames.Value;
                    }
                    if (DdgiTransparencyCandidateLimit.HasValue)
                        settings.DdgiTransparencyCandidateLimit = DdgiTransparencyCandidateLimit.Value;
                    if (DdgiTransparencyLayerLimit.HasValue)
                        settings.DdgiTransparencyLayerLimit = DdgiTransparencyLayerLimit.Value;
                    if (DdgiDecalCandidateLimit.HasValue)
                        settings.DdgiDecalCandidateLimit = DdgiDecalCandidateLimit.Value;
                    if (SimpleDdgiDirectionalRadianceMemoryBudgetBytes.HasValue)
                    {
                        settings.SimpleDdgiDirectionalRadianceMemoryBudgetBytes =
                            SimpleDdgiDirectionalRadianceMemoryBudgetBytes.Value;
                    }
                    if (SimpleDdgiRoughSpecularMinimumRoughness.HasValue)
                    {
                        settings.SimpleDdgiRoughSpecularMinimumRoughness =
                            SimpleDdgiRoughSpecularMinimumRoughness.Value;
                    }
                    if (SimpleDdgiRoughSpecularFullWeightRoughness.HasValue)
                    {
                        settings.SimpleDdgiRoughSpecularFullWeightRoughness =
                            SimpleDdgiRoughSpecularFullWeightRoughness.Value;
                    }
                }
                settings.SimpleDdgiProbeSpacing = SimpleDdgiProbeSpacing;
                settings.SimpleDdgiRingCount = SimpleDdgiRingCount;
                settings.SimpleDdgiRingBaseSpacing = SimpleDdgiRingBaseSpacing;
                settings.SimpleDdgiRingSpacingMultiplier = SimpleDdgiRingSpacingMultiplier;
                settings.SimpleDdgiViewForwardPlacementFraction = SimpleDdgiViewForwardPlacementFraction;
                settings.SimpleDdgiVerticalRingPolicy = SimpleDdgiVerticalRingPolicy;
                settings.SimpleDdgiReceiverVerticalAnchor = SimpleDdgiReceiverVerticalAnchor;
                settings.SimpleDdgiVerticalRecenterHysteresisFraction = SimpleDdgiVerticalRecenterHysteresisFraction;
                settings.SimpleDdgiNearRingGridSizeX = SimpleDdgiNearRingGridSizeX;
                settings.SimpleDdgiNearRingGridSizeY = SimpleDdgiNearRingGridSizeY;
                settings.SimpleDdgiNearRingGridSizeZ = SimpleDdgiNearRingGridSizeZ;
                settings.SimpleDdgiMidRingGridSizeX = SimpleDdgiMidRingGridSizeX;
                settings.SimpleDdgiMidRingGridSizeY = SimpleDdgiMidRingGridSizeY;
                settings.SimpleDdgiMidRingGridSizeZ = SimpleDdgiMidRingGridSizeZ;
                settings.SimpleDdgiFarRingGridSizeX = SimpleDdgiFarRingGridSizeX;
                settings.SimpleDdgiFarRingGridSizeY = SimpleDdgiFarRingGridSizeY;
                settings.SimpleDdgiFarRingGridSizeZ = SimpleDdgiFarRingGridSizeZ;
                if (SimpleDdgiRefinementBricksEnabled.HasValue)
                    settings.SimpleDdgiRefinementBricksEnabled = SimpleDdgiRefinementBricksEnabled.Value;
                if (SimpleDdgiRefinementMaximumBricks.HasValue)
                    settings.SimpleDdgiRefinementMaximumBricks = SimpleDdgiRefinementMaximumBricks.Value;
                if (SimpleDdgiRefinementGridSizeX.HasValue)
                    settings.SimpleDdgiRefinementGridSizeX = SimpleDdgiRefinementGridSizeX.Value;
                if (SimpleDdgiRefinementGridSizeY.HasValue)
                    settings.SimpleDdgiRefinementGridSizeY = SimpleDdgiRefinementGridSizeY.Value;
                if (SimpleDdgiRefinementGridSizeZ.HasValue)
                    settings.SimpleDdgiRefinementGridSizeZ = SimpleDdgiRefinementGridSizeZ.Value;
                if (SimpleDdgiRefinementSpacingScale.HasValue)
                    settings.SimpleDdgiRefinementSpacingScale = SimpleDdgiRefinementSpacingScale.Value;
                if (SimpleDdgiRefinementRetentionFrames.HasValue)
                    settings.SimpleDdgiRefinementRetentionFrames = SimpleDdgiRefinementRetentionFrames.Value;
                if (SimpleDdgiRefinementMinimumEmissiveLuminanceNits.HasValue)
                {
                    settings.SimpleDdgiRefinementMinimumEmissiveLuminanceNits =
                        SimpleDdgiRefinementMinimumEmissiveLuminanceNits.Value;
                }
                if (SimpleDdgiRefinementMaximumEmitterAreaSquareMeters.HasValue)
                {
                    settings.SimpleDdgiRefinementMaximumEmitterAreaSquareMeters =
                        SimpleDdgiRefinementMaximumEmitterAreaSquareMeters.Value;
                }
                if (SimpleDdgiNearVisibilitySidecarEnabled.HasValue)
                {
                    settings.SimpleDdgiNearVisibilitySidecarEnabled =
                        SimpleDdgiNearVisibilitySidecarEnabled.Value;
                }
                if (SimpleDdgiNearVisibilitySidecarMemoryBudgetBytes.HasValue)
                {
                    settings.SimpleDdgiNearVisibilitySidecarMemoryBudgetBytes =
                        SimpleDdgiNearVisibilitySidecarMemoryBudgetBytes.Value;
                }
                settings.SimpleDdgiRaysPerProbe = SimpleDdgiRaysPerProbe;
                settings.SimpleDdgiMaintenanceRaysPerProbe = SimpleDdgiMaintenanceRaysPerProbe;
                settings.SimpleDdgiNearFullRaysPerProbe = SimpleDdgiNearFullRaysPerProbe;
                settings.SimpleDdgiMidFullRaysPerProbe = SimpleDdgiMidFullRaysPerProbe;
                settings.SimpleDdgiFarFullRaysPerProbe = SimpleDdgiFarFullRaysPerProbe;
                settings.SimpleDdgiNearMaintenanceRaysPerProbe = SimpleDdgiNearMaintenanceRaysPerProbe;
                settings.SimpleDdgiMidMaintenanceRaysPerProbe = SimpleDdgiMidMaintenanceRaysPerProbe;
                settings.SimpleDdgiFarMaintenanceRaysPerProbe = SimpleDdgiFarMaintenanceRaysPerProbe;
                settings.SimpleDdgiNearMinimumUpdateQuota = SimpleDdgiNearMinimumUpdateQuota;
                settings.SimpleDdgiMidMinimumUpdateQuota = SimpleDdgiMidMinimumUpdateQuota;
                settings.SimpleDdgiFarMinimumUpdateQuota = SimpleDdgiFarMinimumUpdateQuota;
                settings.SimpleDdgiNearMaximumUpdateQuota = SimpleDdgiNearMaximumUpdateQuota;
                settings.SimpleDdgiMidMaximumUpdateQuota = SimpleDdgiMidMaximumUpdateQuota;
                settings.SimpleDdgiFarMaximumUpdateQuota = SimpleDdgiFarMaximumUpdateQuota;
                settings.SimpleDdgiNearMaterialTextureMaxCascade = SimpleDdgiNearMaterialTextureMaxCascade;
                settings.SimpleDdgiMidMaterialTextureMaxCascade = SimpleDdgiMidMaterialTextureMaxCascade;
                settings.SimpleDdgiFarMaterialTextureMaxCascade = SimpleDdgiFarMaterialTextureMaxCascade;
                if (sourceVersion < 9)
                {
                    settings.SimpleDdgiNearLocalLightSamplesPerHit = SimpleDdgiNearMaxShadedLights;
                    settings.SimpleDdgiMidLocalLightSamplesPerHit = SimpleDdgiMidMaxShadedLights;
                    settings.SimpleDdgiFarLocalLightSamplesPerHit = SimpleDdgiFarMaxShadedLights;
                }
                settings.SimpleDdgiHysteresis = SimpleDdgiHysteresis;
                settings.SimpleDdgiHysteresisChangeThreshold = SimpleDdgiHysteresisChangeThreshold;
                settings.SimpleDdgiHysteresisStepThreshold = SimpleDdgiHysteresisStepThreshold;
                settings.SimpleDdgiLightingDirtyFrameCount = SimpleDdgiLightingDirtyFrameCount;
                settings.SimpleDdgiStableMaintenanceUpdateCount = SimpleDdgiStableMaintenanceUpdateCount;
                settings.SimpleDdgiStableMaintenanceEmaThreshold = SimpleDdgiStableMaintenanceEmaThreshold;
                settings.SimpleDdgiTransportSolverRelaxation = SimpleDdgiTransportSolverRelaxation;
                settings.SimpleDdgiTransportAlbedoClamp = SimpleDdgiTransportAlbedoClamp;
                if (SimpleDdgiTransportTailRelativeTolerance.HasValue)
                {
                    settings.SimpleDdgiTransportTailRelativeTolerance =
                        SimpleDdgiTransportTailRelativeTolerance.Value;
                }
                else if (TryGetLegacyFloat("SimpleDdgiTransportResidualThreshold", out float legacyTailTolerance))
                {
                    settings.SimpleDdgiTransportTailRelativeTolerance = legacyTailTolerance;
                }

                if (SimpleDdgiTransportAcceleratedSweepCount.HasValue)
                    settings.SimpleDdgiTransportAcceleratedSweepCount = SimpleDdgiTransportAcceleratedSweepCount.Value;
                if (SimpleDdgiTransportAccelerationEnabled.HasValue)
                    settings.SimpleDdgiTransportAccelerationEnabled = SimpleDdgiTransportAccelerationEnabled.Value;
                if (SimpleDdgiTransportTailCertificationEnabled.HasValue)
                    settings.SimpleDdgiTransportTailCertificationEnabled = SimpleDdgiTransportTailCertificationEnabled.Value;

                // The old generation count is still loaded for tooling and
                // diagnostics, but it no longer controls V2 retirement.
                if (TryGetLegacyInt("SimpleDdgiTransportMaximumSolverGenerations", out int legacyMaximumGenerations))
                    settings.SimpleDdgiTransportMaximumSolverGenerations = legacyMaximumGenerations;
                settings.SimpleDdgiTransportSourceRefreshFrames = SimpleDdgiTransportSourceRefreshFrames;
                settings.SimpleDdgiAutomaticProbeDensityScale = SimpleDdgiAutomaticProbeDensityScale;
                settings.SimpleDdgiNormalBias = SimpleDdgiNormalBias;
                settings.SimpleDdgiViewBias = SimpleDdgiViewBias;
                settings.SimpleDdgiMaximumWorldBiasMeters = SimpleDdgiMaximumWorldBiasMeters;
                settings.SimpleDdgiArchitecturalThicknessMeters = SimpleDdgiArchitecturalThicknessMeters;
                settings.SimpleDdgiProbeUpdatesPerFrame = SimpleDdgiProbeUpdatesPerFrame;
                settings.FarFieldClipmapEnabled = FarFieldClipmapEnabled;
                settings.FarFieldPagedEnabled = FarFieldPagedEnabled;
                settings.GiFarFieldMaterialV2 = GiFarFieldMaterialV2;
                settings.FarFieldSkyVisibilityEnabled = FarFieldSkyVisibilityEnabled;
                settings.FarFieldSunShadowEnabled = FarFieldSunShadowEnabled;
                settings.FarFieldClipmapResolution = FarFieldClipmapResolution;
                settings.FarFieldStartDistance = FarFieldStartDistance;
                settings.FarFieldMaxTraceSteps = FarFieldMaxTraceSteps;
                settings.FarFieldPageResolution = FarFieldPageResolution;
                settings.FarFieldCascadeCount = FarFieldCascadeCount;
                settings.FarFieldResidentPageBudget = FarFieldResidentPageBudget;
                settings.FarFieldPageUpdatesPerFrame = FarFieldPageUpdatesPerFrame;
                settings.FarFieldPageRequestRadius = FarFieldPageRequestRadius;
                settings.FarFieldBaseVoxelSize = FarFieldBaseVoxelSize;
                settings.FarFieldCascadeVoxelScale = FarFieldCascadeVoxelScale;
                settings.FarFieldMemoryBudgetBytes = FarFieldMemoryBudgetBytes;
                settings.FarFieldForceAll = FarFieldForceAll;
                settings.StreamedGiAccelerationStructuresEnabled = StreamedGiAccelerationStructuresEnabled;
                settings.GiAccelerationStructureMemoryBudgetBytes = GiAccelerationStructureMemoryBudgetBytes;
                settings.GiAccelerationStructureStaticResidentDistance = GiAccelerationStructureStaticResidentDistance;
                settings.GiAccelerationStructureMaximumStaticInstances = GiAccelerationStructureMaximumStaticInstances;
                settings.GiAccelerationStructureEvictionGraceFrames = GiAccelerationStructureEvictionGraceFrames;
                settings.DdgiAsyncComputeEnabled = DdgiAsyncComputeEnabled;
                settings.DdgiProbeUpdatePrimaryRayBudget = DdgiProbeUpdatePrimaryRayBudget;
                settings.DdgiMaxShadedLights = DdgiMaxShadedLights;
                settings.DdgiMaterialTextureMaxCascade = DdgiMaterialTextureMaxCascade;
                settings.DdgiAtlasMemoryBudgetBytes = DdgiAtlasMemoryBudgetBytes;
                settings.DdgiThinWallLeakClampStrength = DdgiThinWallLeakClampStrength;
                settings.DdgiSelfShadowBiasScale = DdgiSelfShadowBiasScale;
                settings.ResolutionScale = ResolutionScale;
                settings.MaxBounceDistance = MaxBounceDistance;
                settings.TemporalEnabled = TemporalEnabled;
                settings.DenoiserEnabled = DenoiserEnabled;
                settings.HistoryResponsiveness = HistoryResponsiveness;
                settings.NormalRejectionThreshold = NormalRejectionThreshold;
                settings.DepthRejectionThreshold = DepthRejectionThreshold;
                settings.LeakClampStrength = LeakClampStrength;
            }

            private bool TryGetLegacyFloat(string name, out float value)
            {
                value = default;
                if (ExtensionData == null)
                    return false;

                foreach (KeyValuePair<string, JsonElement> entry in ExtensionData)
                {
                    if (!string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase) ||
                        !entry.Value.TryGetSingle(out float parsed) ||
                        !float.IsFinite(parsed))
                    {
                        continue;
                    }

                    value = parsed;
                    return true;
                }

                return false;
            }

            private bool TryGetLegacyInt(string name, out int value)
            {
                value = default;
                if (ExtensionData == null)
                    return false;

                foreach (KeyValuePair<string, JsonElement> entry in ExtensionData)
                {
                    if (!string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase) ||
                        !entry.Value.TryGetInt32(out int parsed))
                    {
                        continue;
                    }

                    value = parsed;
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Persisted authored-volume contract. Keep vectors as named scalar
        /// properties: <see cref="Vector3"/> intentionally exposes fields for
        /// interop, while the settings JSON contract must remain field-agnostic
        /// and stable across serializer option changes.
        /// </summary>
        private sealed record SimpleDdgiAuthoredVolumeFile
        {
            public Vector3File? Min { get; init; } = new();
            public Vector3File? Max { get; init; } = new();
            public float Spacing { get; init; } = 1.0f;
            public Vector3File? LatticePhase { get; init; } = new();
            public SimpleDdgiVolumePurpose Purpose { get; init; } = SimpleDdgiVolumePurpose.ReceiverHero;
            public int Priority { get; init; }

            public static SimpleDdgiAuthoredVolumeFile FromSettings(SimpleDdgiAuthoredVolume volume) => new()
            {
                Min = Vector3File.FromVector3(volume.Min),
                Max = Vector3File.FromVector3(volume.Max),
                Spacing = volume.Spacing,
                LatticePhase = Vector3File.FromVector3(volume.LatticePhase),
                Purpose = volume.Purpose,
                Priority = volume.Priority
            };

            public SimpleDdgiAuthoredVolume ToSettings() => new(
                Min?.ToVector3() ?? Vector3.Zero,
                Max?.ToVector3() ?? Vector3.Zero,
                Spacing,
                LatticePhase?.ToVector3() ?? Vector3.Zero,
                Purpose,
                Priority);
        }

        /// <summary>Named scalar serialization for the engine math vector ABI.</summary>
        private sealed record Vector3File
        {
            public float X { get; init; }
            public float Y { get; init; }
            public float Z { get; init; }

            public static Vector3File FromVector3(Vector3 value) => new()
            {
                X = value.X,
                Y = value.Y,
                Z = value.Z
            };

            public Vector3 ToVector3() => new(X, Y, Z);
        }

        private sealed record AsyncComputeFile
        {
            public AsyncComputeMode? Mode { get; init; }
            public AsyncComputePreferredPathMask? PreferredPathMask
                { get; init; }

            // Version 1 persisted a single bool. Keep accepting it, but do not emit it from
            // new files so a modern mode cannot be overwritten by JSON property ordering.
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public bool? Enabled { get; init; }
            public bool HiZBuildEnabled { get; init; } = true;
            public bool AmbientOcclusionBlurEnabled { get; init; } = true;
            public bool FogEnabled { get; init; } = true;
            public bool BloomEnabled { get; init; } = true;
            public bool SimpleDdgiUpdateEnabled { get; init; } = true;
            public bool FarFieldClipmapBakeEnabled { get; init; } = true;
            public bool GpuParticlesEnabled { get; init; } = true;
            public int AutoMinimumSampleCount { get; init; } = 30;
            public int AutoWarmupFrameCount { get; init; } = 60;
            public float AutoMinimumAbsoluteBenefitMilliseconds { get; init; } = 0.25f;
            public float AutoMinimumRelativeBenefit { get; init; } = 0.03f;
            public int AutoDecisionCooldownFrames { get; init; } = 180;

            public static AsyncComputeFile FromSettings(AsyncComputeSettings settings)
            {
                return new AsyncComputeFile
                {
                    Mode = settings.Mode,
                    PreferredPathMask = settings.PreferredPathMask,
                    HiZBuildEnabled = settings.HiZBuildEnabled,
                    AmbientOcclusionBlurEnabled = settings.AmbientOcclusionBlurEnabled,
                    FogEnabled = settings.FogEnabled,
                    BloomEnabled = settings.BloomEnabled,
                    SimpleDdgiUpdateEnabled = settings.SimpleDdgiUpdateEnabled,
                    FarFieldClipmapBakeEnabled = settings.FarFieldClipmapBakeEnabled,
                    GpuParticlesEnabled = settings.GpuParticlesEnabled,
                    AutoMinimumSampleCount = settings.AutoMinimumSampleCount,
                    AutoWarmupFrameCount = settings.AutoWarmupFrameCount,
                    AutoMinimumAbsoluteBenefitMilliseconds = settings.AutoMinimumAbsoluteBenefitMilliseconds,
                    AutoMinimumRelativeBenefit = settings.AutoMinimumRelativeBenefit,
                    AutoDecisionCooldownFrames = settings.AutoDecisionCooldownFrames
                };
            }

            public void ApplyTo(AsyncComputeSettings settings, bool missingModeMeansDisabled)
            {
                settings.Mode = Mode.HasValue && Enum.IsDefined(Mode.Value)
                    ? Mode.Value
                    : Enabled.HasValue
                        ? Enabled.Value
                            ? AsyncComputeMode.ForceEnabledForValidation
                            : AsyncComputeMode.Disabled
                        : missingModeMeansDisabled
                            ? AsyncComputeMode.Disabled
                            : AsyncComputeMode.Auto;
                AsyncComputePreferredPathMask preferred =
                    PreferredPathMask ??
                    (missingModeMeansDisabled
                        ? AsyncComputePreferredPathMask.None
                        : AsyncComputeSettings.DefaultPreferredPathMask);
                settings.PreferredPathMask =
                    preferred & AsyncComputePreferredPathMask.All;
                settings.HiZBuildEnabled = HiZBuildEnabled;
                settings.AmbientOcclusionBlurEnabled = AmbientOcclusionBlurEnabled;
                settings.FogEnabled = FogEnabled;
                settings.BloomEnabled = BloomEnabled;
                settings.SimpleDdgiUpdateEnabled = SimpleDdgiUpdateEnabled;
                settings.FarFieldClipmapBakeEnabled = FarFieldClipmapBakeEnabled;
                settings.GpuParticlesEnabled = GpuParticlesEnabled;
                settings.AutoMinimumSampleCount = Math.Clamp(AutoMinimumSampleCount, 1, 4_096);
                settings.AutoWarmupFrameCount = Math.Clamp(AutoWarmupFrameCount, 0, 16_384);
                settings.AutoMinimumAbsoluteBenefitMilliseconds = ClampFinite(AutoMinimumAbsoluteBenefitMilliseconds, 0.0f, 100.0f, 0.25f);
                settings.AutoMinimumRelativeBenefit = ClampFinite(AutoMinimumRelativeBenefit, 0.0f, 1.0f, 0.03f);
                settings.AutoDecisionCooldownFrames = Math.Clamp(AutoDecisionCooldownFrames, 0, 16_384);
            }

            private static float ClampFinite(float value, float minimum, float maximum, float fallback) =>
                float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
        }

        private sealed record SceneSubmissionFile
        {
            public bool GpuCompactionEnabled { get; init; } = true;
            public bool IndirectMeshletDispatchEnabled { get; init; } = true;
            public bool GpuLodSelectionEnabled { get; init; } = true;
            public GpuLodSelectionMode? GpuLodSelectionMode { get; init; }
            public float? GpuLodTargetPixelError { get; init; }
            public bool? GpuLodDitherTransitionsEnabled { get; init; }
            public int? GpuLodTransitionFrameCount { get; init; }
            public bool? GpuHierarchicalLodEnabled { get; init; }
            public bool? GpuMeshletStreamingEnabled { get; init; }
            public int? GpuMeshletStreamingPhysicalPageCount { get; init; }
            public int? GpuMeshletStreamingUploadBudgetMiB { get; init; }
            public int? GpuMeshletStreamingMaximumRequestsPerFrame { get; init; }
            public int? GpuMeshletStreamingConcurrentReads { get; init; }
            public float GpuLod1DistanceRatio { get; init; } = SceneSubmissionSettings.DefaultGpuLod1DistanceRatio;
            public float GpuLod2DistanceRatio { get; init; } = SceneSubmissionSettings.DefaultGpuLod2DistanceRatio;
            public bool GpuShadowCompactionEnabled { get; init; } = true;
            public int GpuShadowLodBias { get; init; } = SceneSubmissionSettings.DefaultGpuShadowLodBias;
            public bool ValidationCompareCpuGpuLists { get; init; }

            public static SceneSubmissionFile FromSettings(SceneSubmissionSettings settings)
            {
                return new SceneSubmissionFile
                {
                    GpuCompactionEnabled = settings.GpuCompactionEnabled,
                    IndirectMeshletDispatchEnabled = settings.IndirectMeshletDispatchEnabled,
                    GpuLodSelectionEnabled = settings.GpuLodSelectionEnabled,
                    GpuLodSelectionMode = settings.GpuLodSelectionMode,
                    GpuLodTargetPixelError =
                        settings.GpuLodTargetPixelError,
                    GpuLodDitherTransitionsEnabled =
                        settings.GpuLodDitherTransitionsEnabled,
                    GpuLodTransitionFrameCount =
                        settings.GpuLodTransitionFrameCount,
                    GpuHierarchicalLodEnabled =
                        settings.GpuHierarchicalLodEnabled,
                    GpuMeshletStreamingEnabled =
                        settings.GpuMeshletStreamingEnabled,
                    GpuMeshletStreamingPhysicalPageCount =
                        settings.GpuMeshletStreamingPhysicalPageCount,
                    GpuMeshletStreamingUploadBudgetMiB =
                        settings.GpuMeshletStreamingUploadBudgetMiB,
                    GpuMeshletStreamingMaximumRequestsPerFrame =
                        settings.GpuMeshletStreamingMaximumRequestsPerFrame,
                    GpuMeshletStreamingConcurrentReads =
                        settings.GpuMeshletStreamingConcurrentReads,
                    GpuLod1DistanceRatio = settings.GpuLod1DistanceRatio,
                    GpuLod2DistanceRatio = settings.GpuLod2DistanceRatio,
                    GpuShadowCompactionEnabled = settings.GpuShadowCompactionEnabled,
                    GpuShadowLodBias = settings.GpuShadowLodBias,
                    ValidationCompareCpuGpuLists = settings.ValidationCompareCpuGpuLists
                };
            }

            public void ApplyTo(
                SceneSubmissionSettings settings,
                int serializationVersion)
            {
                settings.GpuCompactionEnabled = GpuCompactionEnabled;
                settings.IndirectMeshletDispatchEnabled = IndirectMeshletDispatchEnabled;
                settings.GpuLodSelectionEnabled = GpuLodSelectionEnabled;
                settings.GpuLodSelectionMode = serializationVersion >= 23
                    ? GpuLodSelectionMode ??
                        global::Njulf.Rendering.Data.GpuLodSelectionMode.ScreenSpaceError
                    : global::Njulf.Rendering.Data.GpuLodSelectionMode.ScreenSpaceError;
                settings.GpuLodTargetPixelError = serializationVersion >= 23
                    ? GpuLodTargetPixelError ??
                        SceneSubmissionSettings.DefaultGpuLodTargetPixelError
                    : SceneSubmissionSettings.DefaultGpuLodTargetPixelError;
                settings.GpuLodDitherTransitionsEnabled =
                    serializationVersion >= 24
                        ? GpuLodDitherTransitionsEnabled ?? true
                        : true;
                settings.GpuLodTransitionFrameCount =
                    serializationVersion >= 24
                        ? GpuLodTransitionFrameCount ??
                          SceneSubmissionSettings
                              .DefaultGpuLodTransitionFrameCount
                        : SceneSubmissionSettings
                            .DefaultGpuLodTransitionFrameCount;
                settings.GpuHierarchicalLodEnabled =
                    serializationVersion >= 24
                        ? GpuHierarchicalLodEnabled ?? true
                        : true;
                settings.GpuMeshletStreamingEnabled =
                    serializationVersion >= 24
                        ? GpuMeshletStreamingEnabled ?? true
                        : true;
                settings.GpuMeshletStreamingPhysicalPageCount =
                    GpuMeshletStreamingPhysicalPageCount ??
                    SceneSubmissionSettings
                        .DefaultGpuMeshletStreamingPhysicalPageCount;
                settings.GpuMeshletStreamingUploadBudgetMiB =
                    GpuMeshletStreamingUploadBudgetMiB ??
                    SceneSubmissionSettings
                        .DefaultGpuMeshletStreamingUploadBudgetMiB;
                settings.GpuMeshletStreamingMaximumRequestsPerFrame =
                    GpuMeshletStreamingMaximumRequestsPerFrame ??
                    SceneSubmissionSettings
                        .DefaultGpuMeshletStreamingMaximumRequestsPerFrame;
                settings.GpuMeshletStreamingConcurrentReads =
                    GpuMeshletStreamingConcurrentReads ??
                    SceneSubmissionSettings
                        .DefaultGpuMeshletStreamingConcurrentReads;
                settings.GpuLod1DistanceRatio = GpuLod1DistanceRatio;
                settings.GpuLod2DistanceRatio = GpuLod2DistanceRatio;
                settings.GpuShadowCompactionEnabled = GpuShadowCompactionEnabled;
                settings.GpuShadowLodBias = GpuShadowLodBias;
                settings.ValidationCompareCpuGpuLists = ValidationCompareCpuGpuLists;
            }
        }

        private sealed record HiZOcclusionFile
        {
            public bool Enabled { get; init; } = true;
            public bool AdaptiveEnabled { get; init; } = true;
            public bool PreviousFrameSceneSubmissionEnabled { get; init; } = true;
            public bool CurrentFrameForwardVisibilityEnabled { get; init; } = true;
            public bool ForceOn { get; init; }
            public bool ForceProbe { get; init; }
            public bool ValidateAgainstLegacyPath { get; init; }
            public bool SceneSubmissionPreviousFrameCullingEnabled { get; init; } = true;
            public bool CurrentFrameForwardVisibilityCompactionEnabled { get; init; } = true;
            public int PreviousFrameUvPaddingPixels { get; init; } = 8;
            public float OcclusionBias { get; init; } = 0.0005f;
            public bool DisablePreviousFrameCullingDuringFastCameraMotion { get; init; } = true;
            public float FastCameraMotionDistanceThreshold { get; init; } = 1.0f;
            public float FastCameraMotionForwardDotThreshold { get; init; } = 0.985f;
            public int CameraMotionSuppressionFrames { get; init; } = 1;

            public static HiZOcclusionFile FromSettings(HiZOcclusionSettings settings)
            {
                return new HiZOcclusionFile
                {
                    Enabled = settings.Enabled,
                    AdaptiveEnabled = settings.AdaptiveEnabled,
                    PreviousFrameSceneSubmissionEnabled = settings.PreviousFrameSceneSubmissionEnabled,
                    CurrentFrameForwardVisibilityEnabled = settings.CurrentFrameForwardVisibilityEnabled,
                    ForceOn = settings.ForceOn,
                    ForceProbe = settings.ForceProbe,
                    ValidateAgainstLegacyPath = settings.ValidateAgainstLegacyPath,
                    SceneSubmissionPreviousFrameCullingEnabled = settings.SceneSubmissionPreviousFrameCullingEnabled,
                    CurrentFrameForwardVisibilityCompactionEnabled = settings.CurrentFrameForwardVisibilityCompactionEnabled,
                    PreviousFrameUvPaddingPixels = settings.PreviousFrameUvPaddingPixels,
                    OcclusionBias = settings.OcclusionBias,
                    DisablePreviousFrameCullingDuringFastCameraMotion = settings.DisablePreviousFrameCullingDuringFastCameraMotion,
                    FastCameraMotionDistanceThreshold = settings.FastCameraMotionDistanceThreshold,
                    FastCameraMotionForwardDotThreshold = settings.FastCameraMotionForwardDotThreshold,
                    CameraMotionSuppressionFrames = settings.CameraMotionSuppressionFrames
                };
            }

            public void ApplyTo(HiZOcclusionSettings settings)
            {
                settings.Enabled = Enabled;
                settings.AdaptiveEnabled = AdaptiveEnabled;
                settings.SceneSubmissionPreviousFrameCullingEnabled =
                    PreviousFrameSceneSubmissionEnabled || SceneSubmissionPreviousFrameCullingEnabled;
                settings.CurrentFrameForwardVisibilityCompactionEnabled =
                    CurrentFrameForwardVisibilityEnabled || CurrentFrameForwardVisibilityCompactionEnabled;
                settings.ForceOn = ForceOn;
                settings.ForceProbe = ForceProbe;
                settings.ValidateAgainstLegacyPath = ValidateAgainstLegacyPath;
                settings.PreviousFrameUvPaddingPixels = PreviousFrameUvPaddingPixels;
                settings.OcclusionBias = OcclusionBias;
                settings.DisablePreviousFrameCullingDuringFastCameraMotion = DisablePreviousFrameCullingDuringFastCameraMotion;
                settings.FastCameraMotionDistanceThreshold = FastCameraMotionDistanceThreshold;
                settings.FastCameraMotionForwardDotThreshold = FastCameraMotionForwardDotThreshold;
                settings.CameraMotionSuppressionFrames = CameraMotionSuppressionFrames;
            }
        }

        private sealed record FoliageFile
        {
            public bool Enabled { get; init; } = true;
            public bool HiZCullingEnabled { get; init; } = true;
            public bool CastShadows { get; init; } = true;
            public bool IndirectMeshletDispatchEnabled { get; init; } = true;
            public bool FarImpostorsEnabled { get; init; } = true;
            public bool MotionVectorsEnabled { get; init; } = true;
            public bool LocalShadowsEnabled { get; init; } = true;
            public float GrassShadowDistance { get; init; } = 25f;
            public float GrassShadowDensityScale { get; init; } = 0.5f;
            public float MaxDrawDistance { get; init; } = 250f;
            public float DensityScale { get; init; } = 1f;
            public int MaxVisibleClusters { get; init; } = 262144;
            public int MaxVisibleMeshletDraws { get; init; } = 524288;
            public int MaxLocalShadowedSpotLights { get; init; } = 1;
            public int MaxLocalShadowedPointLights { get; init; } = 1;
            public int MaxLocalShadowClusters { get; init; } = 4096;
            public int MaxLocalShadowMeshletDraws { get; init; } = 8192;
            public FoliageDebugView DebugView { get; init; } = FoliageDebugView.None;

            public static FoliageFile FromSettings(FoliageSettings settings)
            {
                return new FoliageFile
                {
                    Enabled = settings.Enabled,
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
