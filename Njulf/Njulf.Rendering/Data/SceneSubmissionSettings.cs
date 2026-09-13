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
    public enum FoliageDebugView : uint
    {
        None = 0,
        Clusters = 1,
        LodBands = 2,
        DensityFade = 3,
        WindStrength = 4,
        HiZRejectedClusters = 5,
        ShadowCasting = 6,
        AlphaCutoff = 7
    }

    /// <summary>
    /// Controls the conservative, attachment-based Vulkan fragment shading-rate
    /// path. Auto never forces coarse shading: the per-frame safety policy and
    /// GPU classifier can retain 1x1 shading for every tile.
    /// </summary>
    public enum VariableRateShadingMode : uint
    {
        Off = 0,
        Auto = 1
    }

    /// <summary>
    /// Selects a portable mesh-shader output/workgroup contract. Auto uses the
    /// compact 48-vertex/64-primitive taskless path and widens only when loaded
    /// content requires it. CompatibilityTask is the sole explicit task-stage
    /// mode.
    /// </summary>
    public enum MeshShaderTuningMode : uint
    {
        Auto = 0,
        Taskless48V64P64Threads = 1,
        Taskless48V64P128Threads = 2,
        Taskless64V126P64Threads = 3,
        Taskless64V126P128Threads = 4,
        CompatibilityTask = 5
    }

    public sealed class RasterSettings
    {
        private VariableRateShadingMode _variableRateShadingMode;
        private MeshShaderTuningMode _meshShaderTuningMode;

        public VariableRateShadingMode VariableRateShadingMode
        {
            get => _variableRateShadingMode;
            set => _variableRateShadingMode = Enum.IsDefined(value)
                ? value
                : VariableRateShadingMode.Off;
        }

        public MeshShaderTuningMode MeshShaderTuningMode
        {
            get => _meshShaderTuningMode;
            set => _meshShaderTuningMode = Enum.IsDefined(value)
                ? value
                : MeshShaderTuningMode.Auto;
        }
    }

    /// <summary>
    /// Selects the policy used by GPU scene submission to choose a cooked
    /// meshlet LOD. Screen-space error is resolution and field-of-view aware;
    /// LegacyDistance preserves the pre-1.5 cooked-content behavior.
    /// </summary>
    public enum GpuLodSelectionMode : uint
    {
        LegacyDistance = 0,
        ScreenSpaceError = 1
    }

    public sealed class FoliageSettings
    {
        private float _grassShadowDistance = 25f;
        private float _grassShadowDensityScale = 0.5f;
        private float _maxDrawDistance = 250f;
        private float _densityScale = 1f;
        private int _maxVisibleClusters = 262144;
        private int _maxVisibleMeshletDraws = 524288;
        private int _maxLocalShadowedSpotLights = 1;
        private int _maxLocalShadowedPointLights = 1;
        private int _maxLocalShadowClusters = 4096;
        private int _maxLocalShadowMeshletDraws = 8192;

        public bool Enabled { get; set; } = true;
        public bool HiZCullingEnabled { get; set; } = true;
        public bool CastShadows { get; set; } = true;
        public bool IndirectMeshletDispatchEnabled { get; set; } = true;
        public bool FarImpostorsEnabled { get; set; } = true;
        public bool MotionVectorsEnabled { get; set; } = true;
        public bool LocalShadowsEnabled { get; set; } = true;

        public float GrassShadowDistance
        {
            get => _grassShadowDistance;
            set => _grassShadowDistance = Clamp(value, 0.0f, 1000.0f);
        }

        public float GrassShadowDensityScale
        {
            get => _grassShadowDensityScale;
            set => _grassShadowDensityScale = Clamp(value, 0.0f, 1.0f);
        }

        public float MaxDrawDistance
        {
            get => _maxDrawDistance;
            set => _maxDrawDistance = Clamp(value, 0.0f, 10000.0f);
        }

        public float DensityScale
        {
            get => _densityScale;
            set => _densityScale = Clamp(value, 0.0f, 8.0f);
        }

        public int MaxVisibleClusters
        {
            get => _maxVisibleClusters;
            set => _maxVisibleClusters = Clamp(value, 0, 4_194_304);
        }

        public int MaxVisibleMeshletDraws
        {
            get => _maxVisibleMeshletDraws;
            set => _maxVisibleMeshletDraws = Clamp(value, 0, 8_388_608);
        }

        public int MaxLocalShadowedSpotLights
        {
            get => _maxLocalShadowedSpotLights;
            set => _maxLocalShadowedSpotLights = Clamp(value, 0, 8);
        }

        public int MaxLocalShadowedPointLights
        {
            get => _maxLocalShadowedPointLights;
            set => _maxLocalShadowedPointLights = Clamp(value, 0, 4);
        }

        public int MaxLocalShadowClusters
        {
            get => _maxLocalShadowClusters;
            set => _maxLocalShadowClusters = Clamp(value, 0, 262144);
        }

        public int MaxLocalShadowMeshletDraws
        {
            get => _maxLocalShadowMeshletDraws;
            set => _maxLocalShadowMeshletDraws = Clamp(value, 0, 524288);
        }

        public FoliageDebugView DebugView { get; set; } = FoliageDebugView.None;

        private static float Clamp(float value, float min, float max)
        {
            if (!float.IsFinite(value))
                return min;
            if (value < min)
                return min;
            return value > max ? max : value;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
                return min;
            return value > max ? max : value;
        }
    }

    public sealed class SceneSubmissionSettings
    {
        public const float DefaultGpuLod1DistanceRatio = 4.0f;
        public const float DefaultGpuLod2DistanceRatio = 10.0f;
        public const float DefaultGpuLodTargetPixelError = 1.0f;
        public const float GpuLodHysteresisFraction = 0.15f;
        public const int DefaultGpuLodTransitionFrameCount = 8;
        public const int MaximumGpuLodTransitionFrameCount = 16;
        public const int DefaultGpuShadowLodBias = 1;
        public const int DefaultGpuMeshletStreamingPhysicalPageCount = 4096;
        public const int DefaultGpuMeshletStreamingUploadBudgetMiB = 8;
        public const int DefaultGpuMeshletStreamingMaximumRequestsPerFrame =
            4096;
        public const int DefaultGpuMeshletStreamingConcurrentReads = 4;

        private float _gpuLod1DistanceRatio = DefaultGpuLod1DistanceRatio;
        private float _gpuLod2DistanceRatio = DefaultGpuLod2DistanceRatio;
        private GpuLodSelectionMode _gpuLodSelectionMode =
            GpuLodSelectionMode.ScreenSpaceError;
        private float _gpuLodTargetPixelError =
            DefaultGpuLodTargetPixelError;
        private int _gpuShadowLodBias = DefaultGpuShadowLodBias;
        private int _gpuLodTransitionFrameCount =
            DefaultGpuLodTransitionFrameCount;
        private int _gpuMeshletStreamingPhysicalPageCount =
            DefaultGpuMeshletStreamingPhysicalPageCount;
        private int _gpuMeshletStreamingUploadBudgetMiB =
            DefaultGpuMeshletStreamingUploadBudgetMiB;
        private int _gpuMeshletStreamingMaximumRequestsPerFrame =
            DefaultGpuMeshletStreamingMaximumRequestsPerFrame;
        private int _gpuMeshletStreamingConcurrentReads =
            DefaultGpuMeshletStreamingConcurrentReads;

        public bool GpuCompactionEnabled { get; set; } = true;
        public bool IndirectMeshletDispatchEnabled { get; set; } = true;
        public bool GpuLodSelectionEnabled { get; set; } = true;
        public bool GpuLodDitherTransitionsEnabled { get; set; } = true;
        /// <summary>
        /// Enables per-cluster error traversal for meshes carrying the v2
        /// hierarchy. Meshes without hierarchy data retain the flat LOD path.
        /// </summary>
        public bool GpuHierarchicalLodEnabled { get; set; } = true;
        /// <summary>
        /// Enables authenticated 64 KiB static-meshlet page admission. Missing,
        /// corrupt, or over-budget sidecars retain the full-resident path.
        /// Activated static meshes pin coarse fallback pages; skinned meshes
        /// remain fully resident.
        /// </summary>
        public bool GpuMeshletStreamingEnabled { get; set; } = true;

        public int GpuMeshletStreamingPhysicalPageCount
        {
            get => _gpuMeshletStreamingPhysicalPageCount;
            set => _gpuMeshletStreamingPhysicalPageCount =
                Math.Clamp(value, 64, 16_384);
        }

        public int GpuMeshletStreamingUploadBudgetMiB
        {
            get => _gpuMeshletStreamingUploadBudgetMiB;
            set => _gpuMeshletStreamingUploadBudgetMiB =
                Math.Clamp(value, 1, 64);
        }

        public int GpuMeshletStreamingMaximumRequestsPerFrame
        {
            get => _gpuMeshletStreamingMaximumRequestsPerFrame;
            set => _gpuMeshletStreamingMaximumRequestsPerFrame =
                Math.Clamp(value, 256, 65_536);
        }

        public int GpuMeshletStreamingConcurrentReads
        {
            get => _gpuMeshletStreamingConcurrentReads;
            set => _gpuMeshletStreamingConcurrentReads =
                Math.Clamp(value, 1, 16);
        }

        public int GpuLodTransitionFrameCount
        {
            get => _gpuLodTransitionFrameCount;
            set => _gpuLodTransitionFrameCount = Math.Clamp(
                value,
                1,
                MaximumGpuLodTransitionFrameCount);
        }

        public GpuLodSelectionMode GpuLodSelectionMode
        {
            get => _gpuLodSelectionMode;
            set => _gpuLodSelectionMode = Enum.IsDefined(value)
                ? value
                : GpuLodSelectionMode.ScreenSpaceError;
        }

        /// <summary>
        /// Maximum projected geometric deviation admitted for a cooked LOD.
        /// Lower values retain finer geometry for longer.
        /// </summary>
        public float GpuLodTargetPixelError
        {
            get => _gpuLodTargetPixelError;
            set => _gpuLodTargetPixelError =
                ClampGpuLodTargetPixelError(value);
        }

        /// <summary>
        /// Distance-to-bounding-radius ratio at which GPU scene submission switches from LOD0 to LOD1.
        /// </summary>
        public float GpuLod1DistanceRatio
        {
            get => _gpuLod1DistanceRatio;
            set
            {
                _gpuLod1DistanceRatio = ClampGpuLod1DistanceRatio(value);
                if (_gpuLod2DistanceRatio < _gpuLod1DistanceRatio)
                    _gpuLod2DistanceRatio = _gpuLod1DistanceRatio;
            }
        }

        /// <summary>
        /// Distance-to-bounding-radius ratio at which GPU scene submission switches from LOD1 to LOD2.
        /// This is always at least <see cref="GpuLod1DistanceRatio"/>.
        /// </summary>
        public float GpuLod2DistanceRatio
        {
            get => _gpuLod2DistanceRatio;
            set => _gpuLod2DistanceRatio = ClampGpuLod2DistanceRatio(value, _gpuLod1DistanceRatio);
        }

        public bool GpuShadowCompactionEnabled { get; set; } = true;

        /// <summary>
        /// Additional requested LOD levels for GPU-compacted directional-shadow draws.
        /// The current command stream is indexed by LOD0 meshlets, so directional shadows
        /// conservatively retain LOD0 whenever a lower LOD lacks a topology-safe mapping.
        /// The requested lower-LOD count remains visible in directional-shadow diagnostics.
        /// </summary>
        public int GpuShadowLodBias
        {
            get => _gpuShadowLodBias;
            set => _gpuShadowLodBias = Math.Clamp(value, 0, 2);
        }

        public bool ValidationCompareCpuGpuLists { get; set; }

        /// <summary>
        /// Restores the production-qualified meshlet submission path. Quality
        /// presets call this before applying their tier-specific pixel-error
        /// budget; persisted current-schema settings and explicit runtime
        /// overrides remain authoritative when applied afterwards.
        /// </summary>
        public void EnableProductionMeshletFeatures()
        {
            GpuCompactionEnabled = true;
            IndirectMeshletDispatchEnabled = true;
            GpuLodSelectionEnabled = true;
            GpuLodSelectionMode = GpuLodSelectionMode.ScreenSpaceError;
            GpuLodDitherTransitionsEnabled = true;
            GpuLodTransitionFrameCount =
                DefaultGpuLodTransitionFrameCount;
            GpuHierarchicalLodEnabled = true;
            GpuMeshletStreamingEnabled = true;
            GpuShadowCompactionEnabled = true;
            GpuShadowLodBias = DefaultGpuShadowLodBias;
        }

        internal static float ClampGpuLod1DistanceRatio(float value) =>
            ClampFinite(value, minimum: 1.0f, maximum: 64.0f, fallback: DefaultGpuLod1DistanceRatio);

        internal static float ClampGpuLod2DistanceRatio(float value, float gpuLod1DistanceRatio) =>
            Math.Max(
                ClampGpuLod1DistanceRatio(gpuLod1DistanceRatio),
                ClampFinite(value, minimum: 1.0f, maximum: 128.0f, fallback: DefaultGpuLod2DistanceRatio));

        internal static float ClampGpuLodTargetPixelError(float value) =>
            ClampFinite(
                value,
                minimum: 0.125f,
                maximum: 8.0f,
                fallback: DefaultGpuLodTargetPixelError);

        private static float ClampFinite(float value, float minimum, float maximum, float fallback) =>
            float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
    }

    public sealed class HiZOcclusionSettings
    {
        private int _previousFrameUvPaddingPixels = 8;
        private float _occlusionBias = 0.0005f;
        private float _fastCameraMotionDistanceThreshold = 1.0f;
        private float _fastCameraMotionForwardDotThreshold = 0.985f;
        private int _cameraMotionSuppressionFrames = 1;

        public bool SceneSubmissionPreviousFrameCullingEnabled { get; set; } = true;
        public bool CurrentFrameForwardVisibilityCompactionEnabled { get; set; } = true;

        public int PreviousFrameUvPaddingPixels
        {
            get => _previousFrameUvPaddingPixels;
            set => _previousFrameUvPaddingPixels = Math.Clamp(value, 0, 64);
        }

        public float OcclusionBias
        {
            get => _occlusionBias;
            set => _occlusionBias = float.IsFinite(value) ? Math.Clamp(value, 0.0f, 0.1f) : 0.0005f;
        }

        public bool DisablePreviousFrameCullingDuringFastCameraMotion { get; set; } = true;

        public float FastCameraMotionDistanceThreshold
        {
            get => _fastCameraMotionDistanceThreshold;
            set => _fastCameraMotionDistanceThreshold = float.IsFinite(value) ? Math.Clamp(value, 0.0f, 100.0f) : 1.0f;
        }

        public float FastCameraMotionForwardDotThreshold
        {
            get => _fastCameraMotionForwardDotThreshold;
            set => _fastCameraMotionForwardDotThreshold = float.IsFinite(value) ? Math.Clamp(value, -1.0f, 1.0f) : 0.985f;
        }

        public int CameraMotionSuppressionFrames
        {
            get => _cameraMotionSuppressionFrames;
            set => _cameraMotionSuppressionFrames = Math.Clamp(value, 1, 8);
        }

        public bool Enabled { get; set; } = true;
        public bool AdaptiveEnabled { get; set; } = true;

        public bool PreviousFrameSceneSubmissionEnabled
        {
            get => SceneSubmissionPreviousFrameCullingEnabled;
            set => SceneSubmissionPreviousFrameCullingEnabled = value;
        }

        public bool CurrentFrameForwardVisibilityEnabled
        {
            get => CurrentFrameForwardVisibilityCompactionEnabled;
            set => CurrentFrameForwardVisibilityCompactionEnabled = value;
        }

        public bool ForceOn { get; set; }
        public bool ForceProbe { get; set; }
        public bool ValidateAgainstLegacyPath { get; set; }
    }
}
