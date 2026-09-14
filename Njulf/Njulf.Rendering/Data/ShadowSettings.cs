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
    public enum ShadowDebugView : uint
    {
        None = 0,
        CascadeOverlay = 2,
        ShadowMapPreview = 3,
        ReceiverFactor = 4,
        SpotAtlasPreview = 5,
        PointCubemapFacePreview = 6,
        LocalShadowSelection = 7,
        DirectionalRayMask = 8,
        DirectionalRayHitDistance = 9,
        DirectionalRayCandidateCount = 10,
        DirectionalRaySceneResidency = 11,
        DirectionalCsmRayDifference = 12,
        DirectionalHistoryConfidence = 13,
        DirectionalHistoryRejection = 14
    }

    /// <summary>
    /// User-authored directional-shadow intent. The renderer resolves this to an
    /// effective mode only after capability, resource, and ray-scene checks pass.
    /// </summary>
    public enum DirectionalShadowMode : uint
    {
        Cascaded = 0,
        HybridContact = 1,
        RayQueryHard = 2,
        RayQuerySoft = 3
    }

    /// <summary>
    /// Optional short-history stabilization for the CSM receiver result.
    /// Enabled is the ordinary production request and bypasses legacy
    /// qualification manifests while retaining runtime resource/reset gates.
    /// </summary>
    public enum DirectionalCsmTemporalMode : uint
    {
        Disabled = 0,
        Auto = 1,
        DeveloperForce = 2,
        Enabled = 3
    }

    public enum DirectionalShadowFilterMode : uint
    {
        LegacyBoxPcf = 0,
        TentPcf = 1
    }

    public enum DirectionalShadowBiasMode : uint
    {
        Legacy = 0,
        WorldTexelScaled = 1
    }

    /// <summary>
    /// Controls whether every cascade uses the authored PCF radius or keeps a
    /// roughly constant world-space filter footprint as texel size grows.
    /// </summary>
    public enum DirectionalPcfRadiusMode : uint
    {
        Constant = 0,
        WorldSpaceAdaptive = 1
    }

    public sealed class ShadowSettings
    {
        public const int MaxDirectionalCascades = 4;

        /// <summary>Applies only the shadow values authored by the corresponding rendering quality tier.</summary>
        /// <remarks>Preserves specialist overrides not controlled by the tier, including map size and ray mode.</remarks>
        public void ApplyPreset(ShadowQualityPreset preset)
        {
            ApplyRenderQualityPreset(preset switch
            {
                ShadowQualityPreset.Low => RenderQualityPreset.Low,
                ShadowQualityPreset.Medium => RenderQualityPreset.Medium,
                ShadowQualityPreset.High => RenderQualityPreset.High,
                ShadowQualityPreset.Ultra => RenderQualityPreset.Ultra,
                _ => throw new ArgumentOutOfRangeException(nameof(preset))
            });
        }

        internal void ApplyRenderQualityPreset(RenderQualityPreset preset)
        {
            DirectionalCsmTemporalMode = preset == RenderQualityPreset.Low
                ? DirectionalCsmTemporalMode.Disabled : DirectionalCsmTemporalMode.Enabled;
            switch (preset)
            {
                case RenderQualityPreset.Low:
                    DirectionalCascadeCount = 1;
                    DirectionalFilterMode =
                        DirectionalShadowFilterMode.LegacyBoxPcf;
                    DirectionalBiasMode =
                        DirectionalShadowBiasMode.Legacy;
                    DirectionalPcfRadiusMode =
                        DirectionalPcfRadiusMode.Constant;
                    SpotShadowsEnabled = false;
                    MaxShadowedSpotLights = 32;
                    PointShadowsEnabled = false;
                    MaxShadowedPointLights = 32;
                    AreaShadowsEnabled = false;
                    MaxShadowedAreaLights = 0;
                    AreaShadowSampleCount = 1;
                    break;
                case RenderQualityPreset.Medium:
                    DirectionalCascadeCount = 2;
                    DirectionalFilterMode =
                        DirectionalShadowFilterMode.TentPcf;
                    DirectionalBiasMode =
                        DirectionalShadowBiasMode.WorldTexelScaled;
                    DirectionalPcfRadiusMode =
                        DirectionalPcfRadiusMode.WorldSpaceAdaptive;
                    SpotShadowsEnabled = true;
                    PointShadowsEnabled = true;
                    MaxShadowedSpotLights = 32;
                    MaxShadowedPointLights = 32;
                    AreaShadowsEnabled = true;
                    MaxShadowedAreaLights = 1;
                    AreaShadowSampleCount = 1;
                    break;
                case RenderQualityPreset.DdgiHigh:
                    DirectionalCascadeCount = 3;
                    DirectionalFilterMode =
                        DirectionalShadowFilterMode.TentPcf;
                    DirectionalBiasMode =
                        DirectionalShadowBiasMode.WorldTexelScaled;
                    DirectionalPcfRadiusMode =
                        DirectionalPcfRadiusMode.WorldSpaceAdaptive;
                    SpotShadowsEnabled = true;
                    PointShadowsEnabled = true;
                    MaxShadowedSpotLights = 32;
                    MaxShadowedPointLights = 32;
                    AreaShadowsEnabled = true;
                    MaxShadowedAreaLights = Math.Max(
                        MaxShadowedAreaLights,
                        2);
                    AreaShadowSampleCount = 1;
                    break;
                case RenderQualityPreset.Ultra:
                    DirectionalCascadeCount = ShadowSettings.MaxDirectionalCascades;
                    DirectionalFilterMode =
                        DirectionalShadowFilterMode.TentPcf;
                    DirectionalBiasMode =
                        DirectionalShadowBiasMode.WorldTexelScaled;
                    DirectionalPcfRadiusMode =
                        DirectionalPcfRadiusMode.WorldSpaceAdaptive;
                    SpotShadowsEnabled = true;
                    PointShadowsEnabled = true;
                    MaxShadowedSpotLights = 32;
                    MaxShadowedPointLights = 32;
                    AreaShadowsEnabled = true;
                    MaxShadowedAreaLights = 4;
                    AreaShadowSampleCount = 2;
                    break;
                case RenderQualityPreset.High:
                    DirectionalCascadeCount = 2;
                    DirectionalFilterMode =
                        DirectionalShadowFilterMode.TentPcf;
                    DirectionalBiasMode =
                        DirectionalShadowBiasMode.WorldTexelScaled;
                    DirectionalPcfRadiusMode =
                        DirectionalPcfRadiusMode.WorldSpaceAdaptive;
                    SpotShadowsEnabled = true;
                    PointShadowsEnabled = true;
                    MaxShadowedSpotLights = 32;
                    MaxShadowedPointLights = 32;
                    AreaShadowsEnabled = true;
                    MaxShadowedAreaLights = Math.Max(
                        MaxShadowedAreaLights,
                        2);
                    AreaShadowSampleCount = 1;
                    break;
            }
        }


        private uint _directionalShadowMapSize = 2048;
        private int _directionalCascadeCount = 2;
        private int _directionalShadowPreviewCascade;
        private float _maxShadowDistance = 80f;
        private float _directionalCascadeBlendFraction = 0.12f;
        private float _directionalCascadeSplitLambda = 0.5f;
        private float _directionalCasterExtrusionDistance = 80f;
        private float _directionalContactShadowDistance = 3f;
        private DirectionalCsmTemporalMode _directionalCsmTemporalMode;
        private int _directionalSoftRecoveryRayCount = 2;
        private int _directionalSoftHistoryLength = 16;
        private int _directionalSoftSpatialPassCount = 3;
        private int _directionalTransparentSoftRayCount = 4;
        private DirectionalShadowMode _requestedDirectionalShadowMode =
            DirectionalShadowMode.Cascaded;
        private DirectionalShadowFilterMode _directionalFilterMode =
            DirectionalShadowFilterMode.LegacyBoxPcf;
        private DirectionalShadowBiasMode _directionalBiasMode =
            DirectionalShadowBiasMode.Legacy;
        private DirectionalPcfRadiusMode _directionalPcfRadiusMode =
            DirectionalPcfRadiusMode.Constant;
        private float _directionalSoftAngularDiameterScale = 1f;
        private float _normalBias = 0.03f;
        private float _slopeScaledDepthBias = 1.5f;
        private float _constantDepthBias = 0.0005f;
        private int _pcfRadius = 1;
        private int _maxShadowedSpotLights = 32;
        private uint _spotShadowAtlasSize = 4096;
        private uint _spotShadowTileSize = 512;
        private float _spotNormalBias = 0.02f;
        private float _spotConstantDepthBias = 0.0005f;
        private float _spotSlopeScaledDepthBias = 1.5f;
        private int _spotPcfRadius = 1;
        private int _maxShadowedPointLights = 32;
        private int _maxShadowedAreaLights = 2;
        private int _areaShadowSampleCount = 1;
        private uint _pointShadowMapSize = 512;
        private float _pointNormalBias = 0.03f;
        private float _pointConstantDepthBias = 0.001f;
        private float _pointSlopeScaledDepthBias = 1.5f;
        private int _pointPcfRadius = 1;

        public bool DirectionalShadowsEnabled { get; set; } = true;
        public bool SpotShadowsEnabled { get; set; } = true;
        public bool PointShadowsEnabled { get; set; } = true;

        private int _localShadowMemoryBudgetMiB = 256;
        /// <summary>Combined point/spot static and working map budget. Zero disables local maps.</summary>
        public int LocalShadowMemoryBudgetMiB
        {
            get => _localShadowMemoryBudgetMiB;
            set => _localShadowMemoryBudgetMiB = Math.Max(0, value);
        }
        public bool LocalShadowCacheEnabled { get; set; } = true;

        public bool AreaShadowsEnabled { get; set; } = true;
        /// <summary>AMD FidelityFX reconstruction of per-emitter visibility.</summary>
        public bool AreaDenoisingEnabled { get; set; }

        public DirectionalShadowMode RequestedDirectionalShadowMode
        {
            get => _requestedDirectionalShadowMode;
            set => _requestedDirectionalShadowMode = value is
                DirectionalShadowMode.Cascaded or
                DirectionalShadowMode.HybridContact or
                DirectionalShadowMode.RayQueryHard or
                DirectionalShadowMode.RayQuerySoft
                    ? value
                    : DirectionalShadowMode.Cascaded;
        }

        public DirectionalCsmTemporalMode DirectionalCsmTemporalMode
        {
            get => _directionalCsmTemporalMode;
            set => _directionalCsmTemporalMode = value is
                DirectionalCsmTemporalMode.Disabled or
                DirectionalCsmTemporalMode.Auto or
                DirectionalCsmTemporalMode.DeveloperForce or
                DirectionalCsmTemporalMode.Enabled
                    ? value
                    : DirectionalCsmTemporalMode.Disabled;
        }

        /// <summary>
        /// Runtime-only admission supplied by a verified qualification
        /// manifest. Saving settings can never manufacture this authority.
        /// </summary>
        [JsonIgnore]
        public bool DirectionalCsmTemporalQualificationApproved { get; internal set; }

        [JsonIgnore]
        public bool EffectiveDirectionalCsmTemporalEnabled =>
            DirectionalCsmTemporalMode is
                DirectionalCsmTemporalMode.Enabled or
                DirectionalCsmTemporalMode.DeveloperForce or
                // Auto is a durable compatibility value. Schema migration
                // rewrites old Auto requests to Enabled, but treating a live
                // Auto assignment identically prevents a manifest from
                // becoming activation authority again.
                DirectionalCsmTemporalMode.Auto;

        public DirectionalShadowFilterMode DirectionalFilterMode
        {
            get => _directionalFilterMode;
            set => _directionalFilterMode = value is
                DirectionalShadowFilterMode.LegacyBoxPcf or
                DirectionalShadowFilterMode.TentPcf
                    ? value
                    : DirectionalShadowFilterMode.LegacyBoxPcf;
        }

        public DirectionalShadowBiasMode DirectionalBiasMode
        {
            get => _directionalBiasMode;
            set => _directionalBiasMode = value is
                DirectionalShadowBiasMode.Legacy or
                DirectionalShadowBiasMode.WorldTexelScaled
                    ? value
                    : DirectionalShadowBiasMode.Legacy;
        }

        public DirectionalPcfRadiusMode DirectionalPcfRadiusMode
        {
            get => _directionalPcfRadiusMode;
            set => _directionalPcfRadiusMode = value is
                DirectionalPcfRadiusMode.Constant or
                DirectionalPcfRadiusMode.WorldSpaceAdaptive
                    ? value
                    : DirectionalPcfRadiusMode.Constant;
        }

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

        /// <summary>
        /// Directional cascade displayed by <see cref="ShadowDebugView.ShadowMapPreview"/>.
        /// The renderer also clamps this to the currently active cascade count.
        /// </summary>
        public int DirectionalShadowPreviewCascade
        {
            get => _directionalShadowPreviewCascade;
            set => _directionalShadowPreviewCascade = Math.Clamp(value, 0, MaxDirectionalCascades - 1);
        }

        public float MaxShadowDistance
        {
            get => _maxShadowDistance;
            set => _maxShadowDistance = Clamp(value, 1f, 1000f);
        }

        /// <summary>
        /// Fraction of the smaller neighbouring cascade span used to overlap and cross-fade
        /// directional-shadow cascades.  The overlap keeps both receiver projections valid at
        /// a split, rather than treating the boundary as a hard ownership change.
        /// </summary>
        public float DirectionalCascadeBlendFraction
        {
            get => _directionalCascadeBlendFraction;
            set => _directionalCascadeBlendFraction = Clamp(value, 0.02f, 0.30f);
        }

        /// <summary>
        /// Practical split-scheme blend between uniform (0) and logarithmic (1)
        /// camera-frustum partitions.
        /// </summary>
        public float DirectionalCascadeSplitLambda
        {
            get => _directionalCascadeSplitLambda;
            set => _directionalCascadeSplitLambda = Clamp(value, 0f, 1f);
        }

        /// <summary>
        /// Conservative distance searched from a cascade receiver volume toward
        /// the directional light when exact caster bounds are unavailable.
        /// </summary>
        public float DirectionalCasterExtrusionDistance
        {
            get => _directionalCasterExtrusionDistance;
            set => _directionalCasterExtrusionDistance = Clamp(value, 1f, 5000f);
        }

        /// <summary>Maximum ray length used by bounded hybrid contact shadows.</summary>
        public float DirectionalContactShadowDistance
        {
            get => MathF.Min(_directionalContactShadowDistance, MaxShadowDistance);
            set => _directionalContactShadowDistance = Clamp(value, 0.1f, 100f);
        }

        /// <summary>
        /// Artistic multiplier for the environment sun diameter used by
        /// finite-sun ray-query shadows. Zero intentionally resolves the soft
        /// mode to deterministic hard rays.
        /// </summary>
        public float DirectionalSoftAngularDiameterScale
        {
            get => _directionalSoftAngularDiameterScale;
            set => _directionalSoftAngularDiameterScale = float.IsFinite(value)
                ? Math.Clamp(value, 0f, 8f)
                : 1f;
        }

        /// <summary>Additional finite-sun rays used after history rejection.</summary>
        public int DirectionalSoftRecoveryRayCount
        {
            get => _directionalSoftRecoveryRayCount;
            set => _directionalSoftRecoveryRayCount = Math.Clamp(value, 1, 4);
        }

        /// <summary>Maximum accepted finite-sun history age in frames.</summary>
        public int DirectionalSoftHistoryLength
        {
            get => _directionalSoftHistoryLength;
            set => _directionalSoftHistoryLength = Math.Clamp(value, 1, 32);
        }

        /// <summary>Edge-aware A-trous passes for finite-sun visibility.</summary>
        public int DirectionalSoftSpatialPassCount
        {
            get => _directionalSoftSpatialPassCount;
            set => _directionalSoftSpatialPassCount = Math.Clamp(value, 0, 3);
        }

        /// <summary>
        /// Direct finite-sun samples for layered transparent receivers, which
        /// deliberately do not consume opaque screen history.
        /// </summary>
        public int DirectionalTransparentSoftRayCount
        {
            get => _directionalTransparentSoftRayCount;
            set => _directionalTransparentSoftRayCount = Math.Clamp(value, 1, 4);
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
            get => _maxShadowedSpotLights;
            set => _maxShadowedSpotLights = Math.Clamp(value, 0, LightManager.MaxLights);
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
            set => _maxShadowedPointLights = Math.Clamp(value, 0, LightManager.MaxLights);
        }

        /// <summary>Maximum area emitters receiving full-resolution ray-query masks.</summary>
        public int MaxShadowedAreaLights
        {
            get => _maxShadowedAreaLights;
            set => _maxShadowedAreaLights = Math.Clamp(value, 0, 4);
        }

        /// <summary>Visibility samples per selected area emitter and opaque pixel.</summary>
        public int AreaShadowSampleCount
        {
            get => _areaShadowSampleCount;
            set => _areaShadowSampleCount = Math.Clamp(value, 1, 4);
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

        /// <summary>
        /// Diagnostic override that refreshes every active directional static-shadow cascade
        /// every frame. Leave disabled outside cache-lifecycle investigations.
        /// </summary>
        public bool ForceStaticCascadeCacheRefresh { get; set; }

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
