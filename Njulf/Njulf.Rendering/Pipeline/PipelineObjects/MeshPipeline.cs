using System;
using System.IO;
using System.Runtime.InteropServices;
using Njulf.Assets;
using Silk.NET.Core.Native;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline.PipelineObjects
{
    public enum ForwardOpaquePipelineFamily : byte
    {
        Full = 0,
        CompactedFull = 1,
        Simple = 2,
        SimpleFullInput = 3,
        CompactedSimple = 4,
        CompactedSimpleFullInput = 5
    }

    [Flags]
    public enum ForwardOpaquePipelineFeatures : byte
    {
        None = 0,
        ReceiverCache = 1 << 0,
        GlobalIlluminationDisabled = 1 << 1,
        AlphaMaskReceiverFeedback = 1 << 2,
        NearFieldDirectSource = 1 << 3,
        GiCausticReceiver = 1 << 4,
        HybridReflectionReceiver = 1 << 5
    }

    public readonly record struct ForwardOpaquePipelineKey(
        ForwardOpaquePipelineFamily Family,
        ForwardOpaquePipelineFeatures Features)
    {
        public const int FamilyCount = 6;
        public const int FeatureCombinationCount = 64;
        public const int CacheEntryCount =
            FamilyCount * FeatureCombinationCount;

        public int CacheIndex
        {
            get
            {
                int family = (int)Family;
                int features = (int)Features;
                if ((uint)family >= FamilyCount ||
                    (uint)features >= FeatureCombinationCount)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(ForwardOpaquePipelineKey),
                        "Forward pipeline key contains an unknown family or feature bit.");
                }

                return family + FamilyCount * features;
            }
        }

        public bool Has(ForwardOpaquePipelineFeatures feature) =>
            (Features & feature) != 0;
    }

    internal readonly record struct TransparentPipelineSelection(
        VkPipeline Pipeline,
        PipelineLayout Layout,
        bool BindRayScene,
        bool BindReceiverCache);

    public sealed unsafe class MeshPipeline : IDisposable
    {
        private const string EntryPoint = "main";
        internal const uint ForwardReceiverCacheLaneSpecializationConstantId =
            30u;
        internal const uint ForwardPerformanceSpecializationConstantId = 31u;
        internal const uint ForwardReceiverCacheCombinedLane = 0u;
        internal const uint ForwardReceiverCacheAcceptedLane = 1u;
        internal const uint ForwardReceiverCacheExactFallbackLane = 2u;
        private const PerformanceOptimizationFeature
            ForwardPerformanceSpecializationFeatures =
                PerformanceOptimizationFeature
                    .HybridOwnershipProjectionElision |
                PerformanceOptimizationFeature
                    .ScreenLocalReceiverAdmission |
                PerformanceOptimizationFeature.SplitHybridForwardPrograms |
                PerformanceOptimizationFeature.StaticShaderSpecialization |
                PerformanceOptimizationFeature.DirectionalLatticeLoadSharing |
                PerformanceOptimizationFeature.CompactMaskedFeedback |
                PerformanceOptimizationFeature.SparseHybridLobePayload;

        internal static uint ResolveForwardPerformanceSpecializationMask(
            RenderSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            return (uint)(settings.EffectivePerformanceOptimizationFeatures &
                ForwardPerformanceSpecializationFeatures);
        }

        internal static bool UsesForwardPerformanceSpecialization(
            string? fragmentShaderName) =>
            fragmentShaderName?.Contains(
                "cache_required",
                StringComparison.Ordinal) == true;

        private enum DeferredPipelineState
        {
            NotAdmitted,
            Deferred,
            Ready,
            Failed
        }

        private readonly VulkanContext _context;
        private readonly BindlessHeap _bindlessHeap;
        private readonly RaySceneDescriptorBank? _raySceneDescriptors;
        private readonly bool _receiverFeedbackPipelinesEnabled;
        private readonly GiPipelineCacheService? _pipelineCacheService;
        private int _pipelineCompilationBatchGeneration;
        private readonly Action<string, Action>? _runStartupStep;
        private readonly nint _entryPointName;
        private readonly MeshShaderSelection _meshShaderSelection;
        private readonly string _compactedForwardMeshShaderName;
        private readonly string _compactedForwardSimpleMeshShaderName;
        private readonly string _compactedDepthMeshShaderName;
        private readonly string _compactedDepthAlphaMeshShaderName;
        private readonly string _compactedShadowAlphaMeshShaderName;
        private readonly string _compactedMotionVectorMeshShaderName;
        private readonly string _compactedMotionVectorAlphaMeshShaderName;
        private ForwardNearFieldDirectSourcePipelineConfiguration
            _nearFieldDirectSourceConfiguration;
        private ForwardGiCausticReceiverPipelineConfiguration
            _giCausticReceiverConfiguration;
        private ForwardHybridReflectionReceiverPipelineConfiguration
            _hybridReflectionConfiguration;
        private Format _colorFormat;
        private Format _depthFormat;
        private string _forwardTaskShaderName = "forward.task.spv";
        private string? _transparentTaskShaderName = "forward.task.spv";
        private string _transparentMeshShaderName = "forward.mesh.spv";
        private Format? _materialTransportProvenanceFormat;
        private DeferredPipelineState _rayTransparentPipelineState;
        private DeferredPipelineState _rayWeightedOitPipelineState;
        private DeferredPipelineState _alphaMaskReceiverFeedbackPipelineState;
        private DeferredPipelineState _transparentReceiverFeedbackPipelineState;
        private DeferredPipelineState _thinGlassReceiverFeedbackPipelineState;
        private DeferredPipelineState _weightedOitReceiverFeedbackPipelineState;
        private DeferredPipelineState _rayTransparentReceiverFeedbackPipelineState;
        private DeferredPipelineState _rayWeightedOitReceiverFeedbackPipelineState;

        private VkPipeline _depthPipeline;
        private VkPipeline _maskedDepthPipeline;
        private VkPipeline _compactedDepthPipeline;
        private VkPipeline _compactedMaskedDepthPipeline;
        private VkPipeline _shadowDepthPipeline;
        private VkPipeline _shadowAlphaDepthPipeline;
        private VkPipeline _compactedShadowAlphaDepthPipeline;
        private VkPipeline _forwardPipeline;
        private VkPipeline _forwardCompactedPipeline;
        private VkPipeline _forwardSimplePipeline;
        private VkPipeline _forwardSimpleFullInputPipeline;
        private VkPipeline _forwardCompactedSimplePipeline;
        private VkPipeline _forwardCompactedSimpleFullInputPipeline;
        private VkPipeline _forwardNearFieldDirectSourcePipeline;
        private VkPipeline _forwardCompactedNearFieldDirectSourcePipeline;
        private VkPipeline _forwardSimpleNearFieldDirectSourcePipeline;
        private VkPipeline _forwardSimpleFullInputNearFieldDirectSourcePipeline;
        private VkPipeline _forwardCompactedSimpleNearFieldDirectSourcePipeline;
        private VkPipeline _forwardCompactedSimpleFullInputNearFieldDirectSourcePipeline;
        private VkPipeline _forwardReceiverCacheNearFieldDirectSourcePipeline;
        private VkPipeline _forwardCompactedReceiverCacheNearFieldDirectSourcePipeline;
        private VkPipeline _forwardSimpleReceiverCacheNearFieldDirectSourcePipeline;
        private VkPipeline _forwardSimpleFullInputReceiverCacheNearFieldDirectSourcePipeline;
        private VkPipeline _forwardCompactedSimpleReceiverCacheNearFieldDirectSourcePipeline;
        private VkPipeline _forwardCompactedSimpleFullInputReceiverCacheNearFieldDirectSourcePipeline;
        private VkPipeline _forwardGiCausticReceiverPipeline;
        private VkPipeline _forwardCompactedGiCausticReceiverPipeline;
        private VkPipeline _forwardSimpleGiCausticReceiverPipeline;
        private VkPipeline _forwardSimpleFullInputGiCausticReceiverPipeline;
        private VkPipeline _forwardCompactedSimpleGiCausticReceiverPipeline;
        private VkPipeline _forwardCompactedSimpleFullInputGiCausticReceiverPipeline;
        private VkPipeline _forwardReceiverCacheGiCausticReceiverPipeline;
        private VkPipeline _forwardCompactedReceiverCacheGiCausticReceiverPipeline;
        private VkPipeline _forwardSimpleReceiverCacheGiCausticReceiverPipeline;
        private VkPipeline _forwardSimpleFullInputReceiverCacheGiCausticReceiverPipeline;
        private VkPipeline _forwardCompactedSimpleReceiverCacheGiCausticReceiverPipeline;
        private VkPipeline _forwardCompactedSimpleFullInputReceiverCacheGiCausticReceiverPipeline;
        private VkPipeline _forwardCombinedAdvancedGiPipeline;
        private VkPipeline _forwardCompactedCombinedAdvancedGiPipeline;
        private VkPipeline _forwardSimpleCombinedAdvancedGiPipeline;
        private VkPipeline _forwardSimpleFullInputCombinedAdvancedGiPipeline;
        private VkPipeline _forwardCompactedSimpleCombinedAdvancedGiPipeline;
        private VkPipeline _forwardCompactedSimpleFullInputCombinedAdvancedGiPipeline;
        private VkPipeline _forwardReceiverCacheCombinedAdvancedGiPipeline;
        private VkPipeline _forwardCompactedReceiverCacheCombinedAdvancedGiPipeline;
        private VkPipeline _forwardSimpleReceiverCacheCombinedAdvancedGiPipeline;
        private VkPipeline _forwardSimpleFullInputReceiverCacheCombinedAdvancedGiPipeline;
        private VkPipeline _forwardCompactedSimpleReceiverCacheCombinedAdvancedGiPipeline;
        private VkPipeline _forwardCompactedSimpleFullInputReceiverCacheCombinedAdvancedGiPipeline;
        private const int HybridReflectionExactLane = 0;
        private const int HybridReflectionCacheCombinedPipelineLane = 1;
        private const int HybridReflectionCacheAcceptedPipelineLane = 2;
        private const int HybridReflectionCacheFallbackPipelineLane = 3;
        private const int HybridReflectionLaneCount = 4;
        // [exact/cache-combined/cache-accepted/cache-fallback lane,
        //  C4/C5 combination 0..3, base pipeline family 0..5]. The split
        // lanes reuse the cache-required SPIR-V and specialize native code at
        // pipeline creation instead of multiplying embedded shader payloads.
        private readonly VkPipeline[,,] _hybridReflectionPipelines =
            new VkPipeline[HybridReflectionLaneCount, 4, 6];
        private readonly VkPipeline[] _forwardOpaquePipelineCache =
            new VkPipeline[ForwardOpaquePipelineKey.CacheEntryCount];
        private readonly bool[] _forwardOpaquePipelineCacheValid =
            new bool[ForwardOpaquePipelineKey.CacheEntryCount];
        private readonly VkPipeline[] _transparentPartitionPipelineCache =
            new VkPipeline[TransparentPipelineKey.CacheEntryCount];
        private readonly bool[] _transparentPartitionPipelineAttempted =
            new bool[TransparentPipelineKey.CacheEntryCount];
        private readonly string?[] _transparentPartitionPipelineFailures =
            new string?[TransparentPipelineKey.CacheEntryCount];
        private int _forwardOpaquePipelineCacheEntryCount;
        private VkPipeline _forwardReceiverCachePipeline;
        private VkPipeline _forwardCompactedReceiverCachePipeline;
        private VkPipeline _forwardSimpleReceiverCachePipeline;
        private VkPipeline _forwardSimpleFullInputReceiverCachePipeline;
        private VkPipeline _forwardCompactedSimpleReceiverCachePipeline;
        private VkPipeline _forwardCompactedSimpleFullInputReceiverCachePipeline;
        private VkPipeline _forwardReceiverCacheLegacyPipeline;
        private VkPipeline _forwardCompactedReceiverCacheLegacyPipeline;
        private VkPipeline _forwardSimpleReceiverCacheLegacyPipeline;
        private VkPipeline _forwardSimpleFullInputReceiverCacheLegacyPipeline;
        private VkPipeline _forwardCompactedSimpleReceiverCacheLegacyPipeline;
        private VkPipeline
            _forwardCompactedSimpleFullInputReceiverCacheLegacyPipeline;
        private VkPipeline _forwardReceiverCacheDebugPipeline;
        private VkPipeline _forwardCompactedReceiverCacheDebugPipeline;
        private VkPipeline _forwardSimpleReceiverCacheDebugPipeline;
        private VkPipeline _forwardSimpleFullInputReceiverCacheDebugPipeline;
        private VkPipeline _forwardCompactedSimpleReceiverCacheDebugPipeline;
        private VkPipeline
            _forwardCompactedSimpleFullInputReceiverCacheDebugPipeline;
        private VkPipeline _forwardReceiverCacheDiagnosticsPipeline;
        private VkPipeline _forwardCompactedReceiverCacheDiagnosticsPipeline;
        private VkPipeline _forwardSimpleReceiverCacheDiagnosticsPipeline;
        private VkPipeline _forwardSimpleFullInputReceiverCacheDiagnosticsPipeline;
        private VkPipeline _forwardCompactedSimpleReceiverCacheDiagnosticsPipeline;
        private VkPipeline
            _forwardCompactedSimpleFullInputReceiverCacheDiagnosticsPipeline;
        private VkPipeline _forwardAlphaMaskReceiverFeedbackPipeline;
        private VkPipeline _forwardCompactedAlphaMaskReceiverFeedbackPipeline;
        private VkPipeline _forwardSimpleAlphaMaskReceiverFeedbackPipeline;
        private VkPipeline _forwardSimpleFullInputAlphaMaskReceiverFeedbackPipeline;
        private VkPipeline _forwardCompactedSimpleAlphaMaskReceiverFeedbackPipeline;
        private VkPipeline _forwardCompactedSimpleFullInputAlphaMaskReceiverFeedbackPipeline;
        private VkPipeline _forwardGiDisabledPipeline;
        private VkPipeline _forwardCompactedGiDisabledPipeline;
        private VkPipeline _forwardSimpleGiDisabledPipeline;
        private VkPipeline _forwardSimpleFullInputGiDisabledPipeline;
        private VkPipeline _forwardCompactedSimpleGiDisabledPipeline;
        private VkPipeline _forwardCompactedSimpleFullInputGiDisabledPipeline;
        private VkPipeline _transparentForwardPipeline;
        private VkPipeline _thinGlassForwardPipeline;
        private VkPipeline _geometryDecalOverlayPipeline;
        private VkPipeline _weightedOitTransparentPipeline;
        private VkPipeline _rayTransparentForwardPipeline;
        private VkPipeline _rayWeightedOitTransparentPipeline;
        private VkPipeline _rayTransparentReceiverFeedbackPipeline;
        private VkPipeline _rayWeightedOitReceiverFeedbackPipeline;
        private VkPipeline _transparentReceiverFeedbackPipeline;
        private VkPipeline _thinGlassReceiverFeedbackPipeline;
        private VkPipeline _weightedOitReceiverFeedbackPipeline;
        private VkPipeline _motionVectorPipeline;
        private VkPipeline _maskedMotionVectorPipeline;
        private VkPipeline _compactedMotionVectorPipeline;
        private VkPipeline _compactedMaskedMotionVectorPipeline;
        private VkPipeline _sceneOpaqueCompactionPipeline;
        private VkPipeline _sceneOpaqueCompactionDiagnosticsPipeline;
        private VkPipeline _forwardVisibilityCompactionPipeline;
        private PipelineLayout _layout;
        private PipelineLayout _rayTransparentLayout;
        private PipelineLayout _sceneSubmissionComputeLayout;
        private DescriptorSetLayout _forwardReceiverCacheBufferSetLayout;
        private PipelineCache _pipelineCache;
        private bool _disposed;

        public MeshPipeline(
            VulkanContext context,
            BindlessHeap bindlessHeap,
            Format colorFormat,
            Format depthFormat,
            RenderSettings settings,
            ForwardNearFieldDirectSourcePipelineConfiguration
                nearFieldDirectSourceConfiguration = default,
            ForwardGiCausticReceiverPipelineConfiguration
                giCausticReceiverConfiguration = default,
            ForwardHybridReflectionReceiverPipelineConfiguration
                hybridReflectionConfiguration = default,
            bool receiverFeedbackPipelinesEnabled = false,
            RaySceneDescriptorBank? raySceneDescriptors = null,
            GiPipelineCacheService? pipelineCacheService = null,
            Action<string, Action>? runStartupStep = null)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bindlessHeap = bindlessHeap ?? throw new ArgumentNullException(nameof(bindlessHeap));
            _raySceneDescriptors = raySceneDescriptors;
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            RendererMeshletBuildProfile productionProfile =
                RendererMeshletBuildProfiles.Production;
            _meshShaderSelection = MeshShaderPermutationPolicy.Resolve(
                Settings.Raster.MeshShaderTuningMode,
                context.MeshShaderDeviceProperties,
                checked((uint)productionProfile.MaxVertices),
                checked((uint)productionProfile.MaxTriangles));
            MeshShaderPermutation meshPermutation =
                _meshShaderSelection.Permutation;
            _compactedForwardMeshShaderName =
                meshPermutation.SelectTasklessArtifact("forward_compacted");
            _compactedForwardSimpleMeshShaderName =
                meshPermutation.SelectTasklessArtifact(
                    "forward_simple_compacted");
            _compactedDepthMeshShaderName =
                meshPermutation.SelectTasklessArtifact("depth_compacted");
            _compactedDepthAlphaMeshShaderName =
                meshPermutation.SelectTasklessArtifact(
                    "depth_alpha_compacted");
            _compactedShadowAlphaMeshShaderName =
                meshPermutation.SelectTasklessArtifact(
                    "shadow_depth_alpha_compacted");
            _compactedMotionVectorMeshShaderName =
                meshPermutation.SelectTasklessArtifact(
                    "motion_vector_compacted");
            _compactedMotionVectorAlphaMeshShaderName =
                meshPermutation.SelectTasklessArtifact(
                    "motion_vector_alpha_compacted");
            _receiverFeedbackPipelinesEnabled =
                receiverFeedbackPipelinesEnabled;
            _pipelineCacheService = pipelineCacheService;
            _runStartupStep = runStartupStep;
            _nearFieldDirectSourceConfiguration = nearFieldDirectSourceConfiguration;
            _giCausticReceiverConfiguration = giCausticReceiverConfiguration;
            _hybridReflectionConfiguration = hybridReflectionConfiguration;
            _entryPointName = SilkMarshal.StringToPtr(EntryPoint);

            ValidatePushConstantRange((uint)Math.Max(
                Math.Max(
                    Math.Max(Marshal.SizeOf<GPUDepthPushConstants>(), Marshal.SizeOf<GPUForwardPushConstants>()),
                    Marshal.SizeOf<GPUMotionVectorPushConstants>()),
                Math.Max(
                    Marshal.SizeOf<GPUSceneOpaqueCompactionPushConstants>(),
                    Marshal.SizeOf<GPUForwardVisibilityCompactionPushConstants>())));
            CreatePipelineCache();
            CreateForwardReceiverCacheBufferSetLayout();
            CreatePipelineLayout();
            CreateRayTransparentPipelineLayout();
            CreateSceneSubmissionComputeLayout();
            CreatePipelines(colorFormat, depthFormat);
            CreateComputePipelines();
        }

        public VkPipeline DepthPipeline => _depthPipeline;
        public MeshShaderSelection MeshShaderSelection =>
            _meshShaderSelection;
        public bool TasklessSubmissionEnabled =>
            _meshShaderSelection.Permutation.Taskless;
        public VkPipeline MaskedDepthPipeline => _maskedDepthPipeline;
        public VkPipeline CompactedDepthPipeline => _compactedDepthPipeline;
        public VkPipeline CompactedMaskedDepthPipeline =>
            _compactedMaskedDepthPipeline;
        public VkPipeline ShadowDepthPipeline => _shadowDepthPipeline;
        public VkPipeline ShadowAlphaDepthPipeline => _shadowAlphaDepthPipeline;
        public VkPipeline CompactedShadowAlphaDepthPipeline =>
            _compactedShadowAlphaDepthPipeline;
        public VkPipeline ForwardPipeline => _forwardPipeline;
        public VkPipeline ForwardFullMaterialPipeline => _forwardPipeline;
        public VkPipeline ForwardCompactedPipeline => _forwardCompactedPipeline;
        public VkPipeline ForwardSimplePipeline => _forwardSimplePipeline;
        public VkPipeline ForwardSimpleGlobalIblPipeline => _forwardSimplePipeline;
        public VkPipeline ForwardSimpleFullInputGlobalIblPipeline => _forwardSimpleFullInputPipeline;
        public VkPipeline ForwardCompactedSimpleGlobalIblPipeline => _forwardCompactedSimplePipeline;
        public VkPipeline ForwardCompactedSimpleFullInputGlobalIblPipeline => _forwardCompactedSimpleFullInputPipeline;
        public VkPipeline TransparentForwardPipeline
        {
            get
            {
                EnsureTransparentForwardPipeline();
                return _transparentForwardPipeline;
            }
        }
        public VkPipeline ThinGlassForwardPipeline
        {
            get
            {
                EnsureThinGlassForwardPipeline();
                return _thinGlassForwardPipeline;
            }
        }
        public VkPipeline GeometryDecalOverlayPipeline
        {
            get
            {
                EnsureGeometryDecalOverlayPipeline();
                return _geometryDecalOverlayPipeline;
            }
        }
        public VkPipeline WeightedOitTransparentPipeline
        {
            get
            {
                EnsureWeightedOitTransparentPipeline();
                return _weightedOitTransparentPipeline;
            }
        }
        public VkPipeline RayTransparentForwardPipeline =>
            _rayTransparentForwardPipeline;
        public VkPipeline RayWeightedOitTransparentPipeline =>
            _rayWeightedOitTransparentPipeline;
        public VkPipeline RayTransparentReceiverFeedbackPipeline =>
            _rayTransparentReceiverFeedbackPipeline;
        public VkPipeline RayWeightedOitReceiverFeedbackPipeline
            => _rayWeightedOitReceiverFeedbackPipeline;
        internal bool RayTransparentPipelinesAdmitted =>
            _rayTransparentPipelineState is
                DeferredPipelineState.Deferred or DeferredPipelineState.Ready;
        public bool RayTransparentPipelinesAvailable =>
            _rayTransparentLayout.Handle != 0 &&
            _rayTransparentForwardPipeline.Handle != 0;
        public bool RayWeightedOitTransparentPipelineAvailable =>
            RayTransparentPipelinesAvailable &&
            _rayWeightedOitTransparentPipeline.Handle != 0;
        public string RayTransparentPipelineFailureReason { get; private set; } =
            "ray-query transparent pipelines are unavailable";
        public VkPipeline TransparentReceiverFeedbackPipeline =>
            _transparentReceiverFeedbackPipeline;
        public VkPipeline ThinGlassReceiverFeedbackPipeline =>
            _thinGlassReceiverFeedbackPipeline;
        public VkPipeline WeightedOitReceiverFeedbackPipeline
            => _weightedOitReceiverFeedbackPipeline;
        public bool TransparentReceiverFeedbackPipelinesAvailable =>
            _transparentReceiverFeedbackPipeline.Handle != 0;
        public bool AlphaMaskReceiverFeedbackPipelinesAvailable =>
            _forwardAlphaMaskReceiverFeedbackPipeline.Handle != 0 &&
            _forwardCompactedAlphaMaskReceiverFeedbackPipeline.Handle != 0 &&
            _forwardSimpleAlphaMaskReceiverFeedbackPipeline.Handle != 0 &&
            _forwardSimpleFullInputAlphaMaskReceiverFeedbackPipeline.Handle != 0 &&
            _forwardCompactedSimpleAlphaMaskReceiverFeedbackPipeline.Handle != 0 &&
            _forwardCompactedSimpleFullInputAlphaMaskReceiverFeedbackPipeline.Handle != 0;
        public bool GiDisabledPipelinesAvailable =>
            _forwardGiDisabledPipeline.Handle != 0 &&
            _forwardCompactedGiDisabledPipeline.Handle != 0 &&
            _forwardSimpleGiDisabledPipeline.Handle != 0 &&
            _forwardSimpleFullInputGiDisabledPipeline.Handle != 0 &&
            _forwardCompactedSimpleGiDisabledPipeline.Handle != 0 &&
            _forwardCompactedSimpleFullInputGiDisabledPipeline.Handle != 0;
        public string ReceiverFeedbackPipelineFailureReason { get; private set; } =
            "receiver-feedback-pipelines-not-admitted-at-startup";
        public VkPipeline MotionVectorPipeline => _motionVectorPipeline;
        public VkPipeline MaskedMotionVectorPipeline => _maskedMotionVectorPipeline;
        public VkPipeline CompactedMotionVectorPipeline =>
            _compactedMotionVectorPipeline;
        public VkPipeline CompactedMaskedMotionVectorPipeline =>
            _compactedMaskedMotionVectorPipeline;
        public VkPipeline SceneOpaqueCompactionPipeline =>
            GpuMeshletCountersEnabled && _sceneOpaqueCompactionDiagnosticsPipeline.Handle != 0
                ? _sceneOpaqueCompactionDiagnosticsPipeline
                : _sceneOpaqueCompactionPipeline;
        public VkPipeline ForwardVisibilityCompactionPipeline => _forwardVisibilityCompactionPipeline;
        public VkPipeline Pipeline => _forwardPipeline;
        public PipelineLayout Layout => _layout;
        public PipelineLayout RayTransparentLayout => _rayTransparentLayout;
        public PipelineLayout SceneSubmissionComputeLayout => _sceneSubmissionComputeLayout;
        internal DescriptorSetLayout ForwardReceiverCacheBufferSetLayout =>
            _forwardReceiverCacheBufferSetLayout;
        public RenderSettings Settings { get; }
        public bool GpuMeshletCountersEnabled { get; private set; }
        public bool MaterialTransportProvenanceAttachmentEnabled { get; private set; }

        /// <summary>
        /// Materializes the active scene's transparent families. Every
        /// semantic color and exact-feedback pipeline belongs to the
        /// first-present scope. The complete scope adds only performance
        /// variants whose canonical fallback produces equivalent pixels.
        /// </summary>
        internal void PrepareScenePipelineManifest(
            in ScenePipelineManifest manifest,
            TransparencyMode compositionMode,
            bool partitioningEnabled,
            bool receiverFeedbackRequired,
            bool rayVariantsRequired,
            bool decalReceiverCacheRequired,
            ScenePipelinePreparationScope preparationScope)
        {
            if (!Enum.IsDefined(compositionMode))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(compositionMode));
            }
            if (!Enum.IsDefined(preparationScope))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(preparationScope));
            }

            bool prepareSpecializations = preparationScope ==
                ScenePipelinePreparationScope.Complete;

            if (prepareSpecializations)
                PreparePostFirstPresentForwardOpaquePipelines();

            if (prepareSpecializations &&
                manifest.Requires(SceneMaterialPipelineKinds.Masked) &&
                receiverFeedbackRequired)
            {
                TryEnsureAlphaMaskReceiverFeedbackPipelines();
            }

            if (!manifest.HasTransparentSurface)
                return;

            if (compositionMode == TransparencyMode.WeightedBlendedOit)
                EnsureWeightedOitTransparentPipeline();
            else
                EnsureTransparentForwardPipeline();

            // The canonical ray program owns directional visibility,
            // reflections, and full thick-transmission transport. It is the
            // only ray pipeline allowed to gate the first production present.
            bool rayPipelinesReady = false;
            if (rayVariantsRequired)
            {
                rayPipelinesReady = TryEnsureRayTransparentPipelines();
                if (rayPipelinesReady &&
                    compositionMode == TransparencyMode.WeightedBlendedOit)
                {
                    rayPipelinesReady =
                        TryEnsureRayWeightedOitTransparentPipeline();
                }
            }

            if (manifest.Requires(SceneMaterialPipelineKinds.ThinGlass) &&
                compositionMode == TransparencyMode.SortedAlphaBlend)
            {
                EnsureThinGlassForwardPipeline();
            }
            if (manifest.Requires(SceneMaterialPipelineKinds.GeometryDecal) &&
                compositionMode == TransparencyMode.SortedAlphaBlend)
            {
                EnsureGeometryDecalOverlayPipeline();
            }

            if (prepareSpecializations && receiverFeedbackRequired)
            {
                if (compositionMode == TransparencyMode.WeightedBlendedOit)
                {
                    TryEnsureWeightedOitReceiverFeedbackPipeline();
                }
                else
                {
                    TryEnsureTransparentReceiverFeedbackPipeline(
                        thinGlass: false);
                    if (manifest.Requires(
                            SceneMaterialPipelineKinds.ThinGlass))
                    {
                        TryEnsureTransparentReceiverFeedbackPipeline(
                            thinGlass: true);
                    }
                }
            }

            if (prepareSpecializations && rayPipelinesReady &&
                receiverFeedbackRequired &&
                !manifest.Requires(
                    SceneMaterialPipelineKinds.ThickTransmission))
            {
                // The compact B1 ray programs intentionally omit full volume
                // transport. Never publish one as the color pipeline for a
                // scene containing thick transmission.
                if (compositionMode ==
                    TransparencyMode.WeightedBlendedOit)
                {
                    TryEnsureRayWeightedOitReceiverFeedbackPipeline();
                }
                else
                {
                    TryEnsureRayTransparentReceiverFeedbackPipeline();
                }
            }

            if (!prepareSpecializations || !partitioningEnabled)
                return;

            Span<TransparentMaterialClass> materialClasses =
                stackalloc TransparentMaterialClass[
                    TransparentPipelineKey.MaterialClassCount];
            int materialClassCount = 0;
            if (manifest.Requires(
                    SceneMaterialPipelineKinds.OrdinaryTransparent) ||
                manifest.Requires(SceneMaterialPipelineKinds.ThinGlass))
            {
                materialClasses[materialClassCount++] =
                    TransparentMaterialClass.OrdinaryBlend;
            }
            if (manifest.Requires(
                    SceneMaterialPipelineKinds.ThickTransmission))
            {
                materialClasses[materialClassCount++] =
                    TransparentMaterialClass.ThickTransmission;
            }
            if (manifest.Requires(SceneMaterialPipelineKinds.GeometryDecal))
            {
                materialClasses[materialClassCount++] =
                    TransparentMaterialClass.GeometryDecal;
            }

            for (int index = 0; index < materialClassCount; index++)
            {
                TransparentMaterialClass materialClass =
                    materialClasses[index];
                TryResolveTransparentPipeline(
                    new TransparentPipelineKey(
                        materialClass,
                        compositionMode,
                        RaySceneRequired: false,
                        ExactReceiverFeedbackRequired: false,
                        DecalReceiverCacheRequired: false),
                    out _,
                    out _);

                if (rayPipelinesReady)
                {
                    TryResolveTransparentPipeline(
                        new TransparentPipelineKey(
                            materialClass,
                            compositionMode,
                            RaySceneRequired: true,
                            ExactReceiverFeedbackRequired: false,
                            DecalReceiverCacheRequired: false),
                        out _,
                        out _);
                }

                if (materialClass ==
                        TransparentMaterialClass.GeometryDecal &&
                    decalReceiverCacheRequired && !rayVariantsRequired)
                {
                    TryResolveTransparentPipeline(
                        new TransparentPipelineKey(
                            materialClass,
                            compositionMode,
                            RaySceneRequired: false,
                            ExactReceiverFeedbackRequired: false,
                            DecalReceiverCacheRequired: true),
                        out _,
                        out _);
                }
            }
        }

        /// <summary>
        /// Materializes the one advanced opaque MRT program that the production
        /// graph can select before its first present. Render-time resolution is
        /// deliberately lookup-only; a missing required program is therefore a
        /// startup failure instead of an unbounded driver compile in Draw.
        /// </summary>
        internal void PrepareFirstPresentForwardOpaquePipeline()
        {
            if (!RendererBuildConfiguration.FastPipelineStartup)
                return;

            ForwardOpaquePipelineFamily family =
                ResolveEffectiveForwardOpaquePipelineFamily(
                    ForwardOpaquePipelineFamily.Full);
            VkPipeline exactPipeline = ResolveBasePipeline(family);
            if (exactPipeline.Handle == 0)
            {
                throw new InvalidOperationException(
                    "The universal opaque forward pipeline is unavailable during first-present preparation.");
            }

            bool nearField = NearFieldDirectSourceAttachmentEnabled;
            bool caustic = GiCausticReceiverAttachmentEnabled;
            bool traceResolutionNearField = nearField &&
                _nearFieldDirectSourceConfiguration.SourceProducerMode ==
                SimpleDdgiNearFieldSourceProducerMode.TraceResolutionRaster;

            bool prepared;
            if (traceResolutionNearField)
            {
                prepared = TryEnsureNearFieldDirectSourcePipeline(
                    exactPipeline,
                    receiverCacheRequired: false);
                if (prepared && caustic)
                {
                    prepared = TryEnsureGiCausticReceiverPipeline(
                        exactPipeline,
                        receiverCacheRequired: false);
                }
            }
            else if (nearField && caustic &&
                     CombinedAdvancedGiAttachmentEnabled)
            {
                prepared = TryEnsureCombinedAdvancedGiPipeline(
                    exactPipeline,
                    receiverCacheRequired: false);
            }
            else if (nearField)
            {
                prepared = TryEnsureNearFieldDirectSourcePipeline(
                    exactPipeline,
                    receiverCacheRequired: false);
            }
            else if (caustic)
            {
                prepared = TryEnsureGiCausticReceiverPipeline(
                    exactPipeline,
                    receiverCacheRequired: false);
            }
            else
            {
                prepared = true;
            }

            if (!prepared)
            {
                throw new InvalidOperationException(
                    "The active advanced opaque forward pipeline could not be prepared before the first production present.");
            }
        }

        /// <summary>
        /// Builds performance-only receiver-cache variants on the bounded
        /// post-present scheduler. Their absence always retains the exact DDGI
        /// gather path and never starts pipeline creation from command recording.
        /// </summary>
        private void PreparePostFirstPresentForwardOpaquePipelines()
        {
            if (!RendererBuildConfiguration.FastPipelineStartup ||
                MaterialTransportProvenanceAttachmentEnabled)
            {
                return;
            }

            SimpleDdgiReceiverCacheMode requestedMode =
                SimpleDdgiReceiverCachePolicy.ResolveRequestedMode(
                    Settings.GlobalIllumination.SimpleDdgiReceiverCacheMode,
                    Settings.Diagnostics.ForceForwardGiReceiverCacheForBenchmark,
                    Settings.Diagnostics.ForceExactForwardGiGatherForBenchmark);
            if (!requestedMode.UsesCache())
                return;

            ForwardOpaquePipelineFamily family =
                ResolveEffectiveForwardOpaquePipelineFamily(
                    ForwardOpaquePipelineFamily.Full);
            VkPipeline exactPipeline = ResolveBasePipeline(family);
            bool ordinaryPipelineReady;
            if (Settings.GlobalIllumination.DebugView ==
                GlobalIlluminationDebugView.DdgiReceiverCacheRejection)
            {
                ordinaryPipelineReady = TryEnsureReceiverCacheSpecializedPipeline(
                    family,
                    "forward_opaque_ddgi_cache_debug.frag.spv",
                    "forward_opaque_simple_ddgi_cache_debug.frag.spv",
                    "forward_opaque_simple_full_input_ddgi_cache_debug.frag.spv",
                    "Surface-Aware Receiver-Cache Debug",
                    ref _forwardReceiverCacheDebugPipeline,
                    ref _forwardCompactedReceiverCacheDebugPipeline,
                    ref _forwardSimpleReceiverCacheDebugPipeline,
                    ref _forwardSimpleFullInputReceiverCacheDebugPipeline,
                    ref _forwardCompactedSimpleReceiverCacheDebugPipeline,
                    ref _forwardCompactedSimpleFullInputReceiverCacheDebugPipeline);
            }
            else if (requestedMode ==
                     SimpleDdgiReceiverCacheMode.LegacyDepthOnlyBenchmark)
            {
                ordinaryPipelineReady = TryEnsureReceiverCacheSpecializedPipeline(
                    family,
                    "forward_opaque_ddgi_cache_legacy.frag.spv",
                    "forward_opaque_simple_ddgi_cache_legacy.frag.spv",
                    "forward_opaque_simple_full_input_ddgi_cache_legacy.frag.spv",
                    "Legacy Depth-Only Receiver-Cache Benchmark",
                    ref _forwardReceiverCacheLegacyPipeline,
                    ref _forwardCompactedReceiverCacheLegacyPipeline,
                    ref _forwardSimpleReceiverCacheLegacyPipeline,
                    ref _forwardSimpleFullInputReceiverCacheLegacyPipeline,
                    ref _forwardCompactedSimpleReceiverCacheLegacyPipeline,
                    ref _forwardCompactedSimpleFullInputReceiverCacheLegacyPipeline);
            }
            else if (Settings.Diagnostics.DdgiForwardEstimateCountersEnabled)
            {
                ordinaryPipelineReady = TryEnsureReceiverCacheSpecializedPipeline(
                    family,
                    "forward_opaque_ddgi_cache_required_diagnostics.frag.spv",
                    "forward_opaque_simple_ddgi_cache_required_diagnostics.frag.spv",
                    "forward_opaque_simple_full_input_ddgi_cache_required_diagnostics.frag.spv",
                    "Surface-Aware Receiver-Cache Diagnostics",
                    ref _forwardReceiverCacheDiagnosticsPipeline,
                    ref _forwardCompactedReceiverCacheDiagnosticsPipeline,
                    ref _forwardSimpleReceiverCacheDiagnosticsPipeline,
                    ref _forwardSimpleFullInputReceiverCacheDiagnosticsPipeline,
                    ref _forwardCompactedSimpleReceiverCacheDiagnosticsPipeline,
                    ref _forwardCompactedSimpleFullInputReceiverCacheDiagnosticsPipeline);
            }
            else
            {
                ordinaryPipelineReady = TryEnsureReceiverCacheSpecializedPipeline(
                    family,
                    "forward_opaque_ddgi_cache_required.frag.spv",
                    "forward_opaque_simple_ddgi_cache_required.frag.spv",
                    "forward_opaque_simple_full_input_ddgi_cache_required.frag.spv",
                    "Receiver-Cache",
                    ref _forwardReceiverCachePipeline,
                    ref _forwardCompactedReceiverCachePipeline,
                    ref _forwardSimpleReceiverCachePipeline,
                    ref _forwardSimpleFullInputReceiverCachePipeline,
                    ref _forwardCompactedSimpleReceiverCachePipeline,
                    ref _forwardCompactedSimpleFullInputReceiverCachePipeline);
            }

            if (!ordinaryPipelineReady ||
                Settings.GlobalIllumination.DebugView !=
                    GlobalIlluminationDebugView.None ||
                requestedMode ==
                    SimpleDdgiReceiverCacheMode.LegacyDepthOnlyBenchmark ||
                Settings.Diagnostics.DdgiForwardEstimateCountersEnabled)
            {
                return;
            }

            bool nearField = NearFieldDirectSourceAttachmentEnabled;
            bool caustic = GiCausticReceiverAttachmentEnabled;
            bool traceResolutionNearField = nearField &&
                _nearFieldDirectSourceConfiguration.SourceProducerMode ==
                SimpleDdgiNearFieldSourceProducerMode.TraceResolutionRaster;
            if (traceResolutionNearField)
            {
                if (caustic)
                {
                    _ = TryEnsureGiCausticReceiverPipeline(
                        exactPipeline,
                        receiverCacheRequired: true);
                }
                return;
            }

            if (nearField && caustic && CombinedAdvancedGiAttachmentEnabled)
            {
                _ = TryEnsureCombinedAdvancedGiPipeline(
                    exactPipeline,
                    receiverCacheRequired: true);
            }
            else if (nearField)
            {
                _ = TryEnsureNearFieldDirectSourcePipeline(
                    exactPipeline,
                    receiverCacheRequired: true);
            }
            else if (caustic)
            {
                _ = TryEnsureGiCausticReceiverPipeline(
                    exactPipeline,
                    receiverCacheRequired: true);
            }
        }

        internal bool TryResolveTransparentPipeline(
            in TransparentPipelineKey key,
            out TransparentPipelineSelection selection,
            out string failureReason)
        {
            selection = default;
            failureReason = string.Empty;
            int cacheIndex;
            try
            {
                cacheIndex = key.CacheIndex;
            }
            catch (ArgumentOutOfRangeException)
            {
                failureReason = "transparent-pipeline-key-invalid";
                return false;
            }

            if (key.ExactReceiverFeedbackRequired)
            {
                return TryResolveExactTransparentPipeline(
                    key,
                    out selection,
                    out failureReason);
            }

            if (key.MaterialClass ==
                    TransparentMaterialClass.GeometryDecal &&
                key.CompositionMode ==
                    TransparencyMode.SortedAlphaBlend &&
                !key.RaySceneRequired &&
                !key.DecalReceiverCacheRequired)
            {
                EnsureGeometryDecalOverlayPipeline();
                if (_geometryDecalOverlayPipeline.Handle == 0)
                {
                    failureReason =
                        "transparent-geometry-decal-overlay-unavailable";
                    return false;
                }

                selection = new TransparentPipelineSelection(
                    _geometryDecalOverlayPipeline,
                    _layout,
                    BindRayScene: false,
                    BindReceiverCache: false);
                return true;
            }

            VkPipeline cachedPipeline =
                _transparentPartitionPipelineCache[cacheIndex];
            if (cachedPipeline.Handle != 0)
            {
                selection = CreateTransparentPipelineSelection(
                    key,
                    cachedPipeline);
                return true;
            }
            if (_transparentPartitionPipelineAttempted[cacheIndex])
            {
                failureReason =
                    _transparentPartitionPipelineFailures[cacheIndex] ??
                    "transparent-specialized-pipeline-unavailable";
                return false;
            }

            _transparentPartitionPipelineAttempted[cacheIndex] = true;
            if (key.RaySceneRequired &&
                (_rayTransparentLayout.Handle == 0 ||
                 !TryEnsureRayTransparentPipelines()))
            {
                failureReason = RayTransparentPipelineFailureReason;
                _transparentPartitionPipelineFailures[cacheIndex] =
                    failureReason;
                return false;
            }

            try
            {
                string fragmentShader =
                    ResolveTransparentPartitionFragmentShader(key);
                PipelineLayout layout = key.RaySceneRequired
                    ? _rayTransparentLayout
                    : _layout;
                VkPipeline pipeline = key.CompositionMode ==
                        TransparencyMode.WeightedBlendedOit
                    ? CreateWeightedOitGraphicsPipeline(
                        _transparentTaskShaderName,
                        _transparentMeshShaderName,
                        fragmentShader,
                        RenderTargetManager
                            .WeightedOitAccumulationFormat,
                        RenderTargetManager
                            .WeightedOitRevealageFormat,
                        _depthFormat,
                        layout)
                    : CreateGraphicsPipeline(
                        _transparentTaskShaderName,
                        _transparentMeshShaderName,
                        fragmentShader,
                        _colorFormat,
                        _depthFormat,
                        hasColorAttachment: true,
                        depthWriteEnable: false,
                        blendEnable: true,
                        cullMode: CullModeFlags.None,
                        depthBiasEnable: false,
                        pipelineLayout: layout);
                _context.SetDebugName(
                    pipeline.Handle,
                    ObjectType.Pipeline,
                    "Transparent Partition " + key);
                _transparentPartitionPipelineCache[cacheIndex] = pipeline;
                selection = CreateTransparentPipelineSelection(
                    key,
                    pipeline);
                return true;
            }
            catch (Exception exception) when (
                exception is VulkanException or IOException or
                ArgumentException or InvalidOperationException)
            {
                failureReason =
                    "transparent-specialized-pipeline-creation-failed:" +
                    exception.GetType().Name + ":" + exception.Message;
                _transparentPartitionPipelineFailures[cacheIndex] =
                    failureReason;
                System.Diagnostics.Debug.WriteLine(
                    "Transparent partition variant unavailable; " +
                    failureReason);
                return false;
            }
        }

        internal static string ResolveTransparentPartitionFragmentShader(
            in TransparentPipelineKey key)
        {
            string prefix = key.CompositionMode ==
                    TransparencyMode.WeightedBlendedOit
                ? "forward_weighted_oit_"
                : "forward_transparent_";
            if (key.DecalReceiverCacheRequired)
                return prefix + "decal_cache_required.frag.spv";

            string materialRole = key.MaterialClass switch
            {
                TransparentMaterialClass.GeometryDecal => "decal",
                TransparentMaterialClass.OrdinaryBlend => "ordinary",
                TransparentMaterialClass.ThickTransmission => "thick",
                _ => throw new ArgumentOutOfRangeException(nameof(key))
            };
            return prefix + materialRole +
                (key.RaySceneRequired ? "_ray" : string.Empty) +
                ".frag.spv";
        }

        private bool TryResolveExactTransparentPipeline(
            in TransparentPipelineKey key,
            out TransparentPipelineSelection selection,
            out string failureReason)
        {
            selection = default;
            failureReason = string.Empty;
            bool weighted = key.CompositionMode ==
                TransparencyMode.WeightedBlendedOit;
            bool available;
            VkPipeline pipeline;
            PipelineLayout layout;
            if (weighted)
            {
                available = key.RaySceneRequired
                    ? _rayWeightedOitReceiverFeedbackPipeline.Handle != 0
                    : _weightedOitReceiverFeedbackPipeline.Handle != 0;
                pipeline = key.RaySceneRequired
                    ? _rayWeightedOitReceiverFeedbackPipeline
                    : _weightedOitReceiverFeedbackPipeline;
            }
            else
            {
                available = key.RaySceneRequired
                    ? _rayTransparentReceiverFeedbackPipeline.Handle != 0
                    : _transparentReceiverFeedbackPipeline.Handle != 0;
                pipeline = key.RaySceneRequired
                    ? _rayTransparentReceiverFeedbackPipeline
                    : _transparentReceiverFeedbackPipeline;
            }

            layout = key.RaySceneRequired
                ? _rayTransparentLayout
                : _layout;
            if (!available || pipeline.Handle == 0)
            {
                failureReason = ReceiverFeedbackPipelineFailureReason;
                return false;
            }

            selection = new TransparentPipelineSelection(
                pipeline,
                layout,
                key.RaySceneRequired,
                BindReceiverCache: false);
            return true;
        }

        private TransparentPipelineSelection
            CreateTransparentPipelineSelection(
                in TransparentPipelineKey key,
                VkPipeline pipeline) =>
            new(
                pipeline,
                key.RaySceneRequired ? _rayTransparentLayout : _layout,
                key.RaySceneRequired,
                key.DecalReceiverCacheRequired);
        /// <summary>
        /// True when construction received a validated C5-effective source
        /// configuration. Development creates the matching material variant on
        /// first use; diagnostic and release tiers build the full set eagerly.
        /// </summary>
        public bool NearFieldDirectSourceAttachmentEnabled { get; private set; }
        public string NearFieldDirectSourceFailureReason { get; private set; } =
            "near-field-direct-source-disabled";
        public ForwardNearFieldDirectSourcePipelineConfiguration
            NearFieldDirectSourceConfiguration => _nearFieldDirectSourceConfiguration;
        public bool GiCausticReceiverAttachmentEnabled { get; private set; }
        public string GiCausticReceiverFailureReason { get; private set; } =
            "caustic-forward-receiver-disabled";
        public ForwardGiCausticReceiverPipelineConfiguration
            GiCausticReceiverConfiguration => _giCausticReceiverConfiguration;
        public bool CombinedAdvancedGiAttachmentEnabled { get; private set; }
        public string CombinedAdvancedGiFailureReason { get; private set; } =
            "combined-advanced-GI-disabled";
        public bool HybridReflectionAttachmentEnabled { get; private set; }
        public string HybridReflectionFailureReason { get; private set; } =
            "hybrid-reflection-receiver-disabled";

        public bool TryResolveHybridReflectionPipeline(
            VkPipeline exactPipeline,
            bool nearFieldDirectSourceEnabled,
            bool giCausticReceiverEnabled,
            bool receiverCacheRequired,
            out VkPipeline hybridPipeline)
        {
            hybridPipeline = default;
            if (!HybridReflectionAttachmentEnabled ||
                !TryResolveBasePipelineFamily(exactPipeline, out int family))
            {
                return false;
            }

            int combination = (giCausticReceiverEnabled ? 1 : 0) |
                (nearFieldDirectSourceEnabled ? 2 : 0);
            int receiver = receiverCacheRequired
                ? HybridReflectionCacheCombinedPipelineLane
                : HybridReflectionExactLane;
            hybridPipeline =
                _hybridReflectionPipelines[receiver, combination, family];
            return hybridPipeline.Handle != 0;
        }

        public bool TryResolveHybridReflectionCacheSplitPipelines(
            ForwardOpaquePipelineFamily requestedFamily,
            bool nearFieldDirectSourceEnabled,
            bool giCausticReceiverEnabled,
            out VkPipeline acceptedPipeline,
            out VkPipeline fallbackPipeline)
        {
            acceptedPipeline = default;
            fallbackPipeline = default;
            if (!HybridReflectionAttachmentEnabled ||
                !Settings.IsPerformanceOptimizationEnabled(
                    PerformanceOptimizationFeature.SplitHybridForwardPrograms))
            {
                return false;
            }

            int family = (int)ResolveEffectiveForwardOpaquePipelineFamily(
                requestedFamily);
            int combination = (giCausticReceiverEnabled ? 1 : 0) |
                (nearFieldDirectSourceEnabled ? 2 : 0);
            acceptedPipeline = _hybridReflectionPipelines[
                HybridReflectionCacheAcceptedPipelineLane,
                combination,
                family];
            fallbackPipeline = _hybridReflectionPipelines[
                HybridReflectionCacheFallbackPipelineLane,
                combination,
                family];
            return acceptedPipeline.Handle != 0 &&
                fallbackPipeline.Handle != 0;
        }

        public bool AreHybridReflectionPipelinesReady(
            bool nearFieldDirectSourceEnabled,
            bool giCausticReceiverEnabled)
        {
            if (!HybridReflectionAttachmentEnabled)
                return false;

            int combination = (giCausticReceiverEnabled ? 1 : 0) |
                (nearFieldDirectSourceEnabled ? 2 : 0);
            int firstFamily = RendererBuildConfiguration.FastPipelineStartup
                ? TasklessSubmissionEnabled ? 1 : 0
                : 0;
            int familyCount = RendererBuildConfiguration.FastPipelineStartup
                ? 1
                : 6;
            int requiredLaneCount = Settings.IsPerformanceOptimizationEnabled(
                PerformanceOptimizationFeature.SplitHybridForwardPrograms)
                ? HybridReflectionLaneCount
                : HybridReflectionCacheAcceptedPipelineLane;
            for (int receiver = 0;
                 receiver < requiredLaneCount;
                 receiver++)
            {
                for (int family = firstFamily;
                     family < firstFamily + familyCount;
                     family++)
                {
                    if (_hybridReflectionPipelines[receiver, combination, family]
                            .Handle == 0)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        public bool TryPrepareHybridReflectionPipelines(
            bool nearFieldDirectSourceEnabled,
            bool giCausticReceiverEnabled)
        {
            if (!HybridReflectionAttachmentEnabled)
                return false;

            int combination = (giCausticReceiverEnabled ? 1 : 0) |
                (nearFieldDirectSourceEnabled ? 2 : 0);
            int firstFamily = RendererBuildConfiguration.FastPipelineStartup
                ? TasklessSubmissionEnabled ? 1 : 0
                : 0;
            int familyCount = RendererBuildConfiguration.FastPipelineStartup
                ? 1
                : 6;
            int requiredLaneCount = Settings.IsPerformanceOptimizationEnabled(
                PerformanceOptimizationFeature.SplitHybridForwardPrograms)
                ? HybridReflectionLaneCount
                : HybridReflectionCacheAcceptedPipelineLane;
            for (int receiver = 0;
                 receiver < requiredLaneCount;
                 receiver++)
            {
                for (int family = firstFamily;
                     family < firstFamily + familyCount;
                     family++)
                {
                    if (_hybridReflectionPipelines[
                                receiver,
                                combination,
                                family].Handle == 0 &&
                        !TryCreateHybridReflectionPipeline(
                            combination,
                            family,
                            receiverLane: receiver))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private bool TryResolveBasePipelineFamily(
            VkPipeline pipeline,
            out int family)
        {
            if (pipeline.Handle == _forwardPipeline.Handle)
                family = 0;
            else if (pipeline.Handle == _forwardCompactedPipeline.Handle)
                family = 1;
            else if (pipeline.Handle == _forwardSimplePipeline.Handle)
                family = 2;
            else if (pipeline.Handle == _forwardSimpleFullInputPipeline.Handle)
                family = 3;
            else if (pipeline.Handle == _forwardCompactedSimplePipeline.Handle)
                family = 4;
            else if (pipeline.Handle ==
                     _forwardCompactedSimpleFullInputPipeline.Handle)
                family = 5;
            else
            {
                family = -1;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Resolves the complete opaque-forward semantic key once, then serves
        /// subsequent draws from a fixed-size handle cache. This replaces the
        /// repeated family/feature decision chain on every bucket submission.
        /// </summary>
        public bool TryResolveForwardOpaquePipeline(
            in ForwardOpaquePipelineKey key,
            out VkPipeline pipeline)
        {
            int cacheIndex = key.CacheIndex;
            if (_forwardOpaquePipelineCacheValid[cacheIndex])
            {
                pipeline = _forwardOpaquePipelineCache[cacheIndex];
                return pipeline.Handle != 0;
            }

            VkPipeline exactPipeline = ResolveBasePipeline(key.Family);
            bool receiverCache = key.Has(
                ForwardOpaquePipelineFeatures.ReceiverCache);
            bool nearField = key.Has(
                ForwardOpaquePipelineFeatures.NearFieldDirectSource);
            bool caustic = key.Has(
                ForwardOpaquePipelineFeatures.GiCausticReceiver);

            bool resolved;
            if (key.Has(
                    ForwardOpaquePipelineFeatures.HybridReflectionReceiver))
            {
                resolved = TryResolveHybridReflectionPipeline(
                    exactPipeline,
                    nearField,
                    caustic,
                    receiverCache,
                    out pipeline);
            }
            else if (nearField && caustic)
            {
                resolved = TryResolveCombinedAdvancedGiPipeline(
                    exactPipeline,
                    receiverCache,
                    out pipeline);
            }
            else if (nearField)
            {
                resolved = TryResolveNearFieldDirectSourcePipeline(
                    exactPipeline,
                    receiverCache,
                    out pipeline);
            }
            else if (caustic)
            {
                resolved = TryResolveGiCausticReceiverPipeline(
                    exactPipeline,
                    receiverCache,
                    out pipeline);
            }
            else
            {
                pipeline = ResolveOpaqueSpecializedPipeline(
                    exactPipeline,
                    receiverCache,
                    key.Has(
                        ForwardOpaquePipelineFeatures
                            .GlobalIlluminationDisabled),
                    key.Has(
                        ForwardOpaquePipelineFeatures
                            .AlphaMaskReceiverFeedback));
                resolved = pipeline.Handle != 0;
            }

            if (!resolved || pipeline.Handle == 0)
                return false;

            _forwardOpaquePipelineCache[cacheIndex] = pipeline;
            _forwardOpaquePipelineCacheValid[cacheIndex] = true;
            _forwardOpaquePipelineCacheEntryCount++;
            return true;
        }

        internal int ForwardOpaquePipelineCacheEntryCount =>
            _forwardOpaquePipelineCacheEntryCount;

        private bool TryEnsureReceiverCacheSpecializedPipeline(
            ForwardOpaquePipelineFamily family,
            string fullFragmentShaderName,
            string simpleFragmentShaderName,
            string simpleFullInputFragmentShaderName,
            string debugVariantName,
            ref VkPipeline fullPipeline,
            ref VkPipeline compactedPipeline,
            ref VkPipeline simplePipeline,
            ref VkPipeline simpleFullInputPipeline,
            ref VkPipeline compactedSimplePipeline,
            ref VkPipeline compactedSimpleFullInputPipeline)
        {
            return family switch
            {
                ForwardOpaquePipelineFamily.Full =>
                    TryEnsureReceiverCacheSpecializedPipeline(
                        ref fullPipeline,
                        _forwardTaskShaderName,
                        "forward.mesh.spv",
                        fullFragmentShaderName,
                        debugVariantName),
                ForwardOpaquePipelineFamily.CompactedFull =>
                    TryEnsureReceiverCacheSpecializedPipeline(
                        ref compactedPipeline,
                        null,
                        _compactedForwardMeshShaderName,
                        fullFragmentShaderName,
                        debugVariantName),
                ForwardOpaquePipelineFamily.Simple =>
                    TryEnsureReceiverCacheSpecializedPipeline(
                        ref simplePipeline,
                        _forwardTaskShaderName,
                        "forward_simple.mesh.spv",
                        simpleFragmentShaderName,
                        debugVariantName),
                ForwardOpaquePipelineFamily.SimpleFullInput =>
                    TryEnsureReceiverCacheSpecializedPipeline(
                        ref simpleFullInputPipeline,
                        _forwardTaskShaderName,
                        "forward.mesh.spv",
                        simpleFullInputFragmentShaderName,
                        debugVariantName),
                ForwardOpaquePipelineFamily.CompactedSimple =>
                    TryEnsureReceiverCacheSpecializedPipeline(
                        ref compactedSimplePipeline,
                        null,
                        _compactedForwardSimpleMeshShaderName,
                        simpleFragmentShaderName,
                        debugVariantName),
                ForwardOpaquePipelineFamily.CompactedSimpleFullInput =>
                    TryEnsureReceiverCacheSpecializedPipeline(
                        ref compactedSimpleFullInputPipeline,
                        null,
                        _compactedForwardMeshShaderName,
                        simpleFullInputFragmentShaderName,
                        debugVariantName),
                _ => false
            };
        }

        private bool TryEnsureReceiverCacheSpecializedPipeline(
            ref VkPipeline pipeline,
            string? taskShaderName,
            string meshShaderName,
            string fragmentShaderName,
            string debugVariantName)
        {
            if (pipeline.Handle != 0)
                return true;

            try
            {
                pipeline = CreateGraphicsPipeline(
                    taskShaderName,
                    meshShaderName,
                    fragmentShaderName,
                    _colorFormat,
                    _depthFormat,
                    hasColorAttachment: true,
                    depthWriteEnable: false,
                    blendEnable: false,
                    cullMode: CullModeFlags.None,
                    depthBiasEnable: false);
                _context.SetDebugName(
                    pipeline.Handle,
                    ObjectType.Pipeline,
                    $"Deferred Opaque Forward Plus {debugVariantName} Mesh Pipeline");
                return pipeline.Handle != 0;
            }
            catch (Exception exception) when (
                exception is VulkanException or IOException or
                ArgumentException or InvalidOperationException)
            {
                DestroyOptionalPipeline(ref pipeline);
                System.Diagnostics.Debug.WriteLine(
                    $"Deferred {debugVariantName} pipeline unavailable: " +
                    $"{exception.GetType().Name}: {exception.Message}");
                return false;
            }
        }

        public bool TryResolveReceiverCacheDiagnosticsPipeline(
            ForwardOpaquePipelineFamily family,
            out VkPipeline pipeline)
        {
            family = ResolveEffectiveForwardOpaquePipelineFamily(family);
            pipeline = family switch
            {
                ForwardOpaquePipelineFamily.Full =>
                    _forwardReceiverCacheDiagnosticsPipeline,
                ForwardOpaquePipelineFamily.CompactedFull =>
                    _forwardCompactedReceiverCacheDiagnosticsPipeline,
                ForwardOpaquePipelineFamily.Simple =>
                    _forwardSimpleReceiverCacheDiagnosticsPipeline,
                ForwardOpaquePipelineFamily.SimpleFullInput =>
                    _forwardSimpleFullInputReceiverCacheDiagnosticsPipeline,
                ForwardOpaquePipelineFamily.CompactedSimple =>
                    _forwardCompactedSimpleReceiverCacheDiagnosticsPipeline,
                ForwardOpaquePipelineFamily.CompactedSimpleFullInput =>
                    _forwardCompactedSimpleFullInputReceiverCacheDiagnosticsPipeline,
                _ => default
            };
            return pipeline.Handle != 0;
        }

        public bool TryResolveReceiverCacheLegacyPipeline(
            ForwardOpaquePipelineFamily family,
            out VkPipeline pipeline)
        {
            family = ResolveEffectiveForwardOpaquePipelineFamily(family);
            pipeline = family switch
            {
                ForwardOpaquePipelineFamily.Full =>
                    _forwardReceiverCacheLegacyPipeline,
                ForwardOpaquePipelineFamily.CompactedFull =>
                    _forwardCompactedReceiverCacheLegacyPipeline,
                ForwardOpaquePipelineFamily.Simple =>
                    _forwardSimpleReceiverCacheLegacyPipeline,
                ForwardOpaquePipelineFamily.SimpleFullInput =>
                    _forwardSimpleFullInputReceiverCacheLegacyPipeline,
                ForwardOpaquePipelineFamily.CompactedSimple =>
                    _forwardCompactedSimpleReceiverCacheLegacyPipeline,
                ForwardOpaquePipelineFamily.CompactedSimpleFullInput =>
                    _forwardCompactedSimpleFullInputReceiverCacheLegacyPipeline,
                _ => default
            };
            return pipeline.Handle != 0;
        }

        public bool TryResolveReceiverCacheDebugPipeline(
            ForwardOpaquePipelineFamily family,
            out VkPipeline pipeline)
        {
            family = ResolveEffectiveForwardOpaquePipelineFamily(family);
            pipeline = family switch
            {
                ForwardOpaquePipelineFamily.Full =>
                    _forwardReceiverCacheDebugPipeline,
                ForwardOpaquePipelineFamily.CompactedFull =>
                    _forwardCompactedReceiverCacheDebugPipeline,
                ForwardOpaquePipelineFamily.Simple =>
                    _forwardSimpleReceiverCacheDebugPipeline,
                ForwardOpaquePipelineFamily.SimpleFullInput =>
                    _forwardSimpleFullInputReceiverCacheDebugPipeline,
                ForwardOpaquePipelineFamily.CompactedSimple =>
                    _forwardCompactedSimpleReceiverCacheDebugPipeline,
                ForwardOpaquePipelineFamily.CompactedSimpleFullInput =>
                    _forwardCompactedSimpleFullInputReceiverCacheDebugPipeline,
                _ => default
            };
            return pipeline.Handle != 0;
        }

        private VkPipeline ResolveBasePipeline(
            ForwardOpaquePipelineFamily family)
        {
            if (RendererBuildConfiguration.FastPipelineStartup)
            {
                VkPipeline universalPipeline = TasklessSubmissionEnabled
                    ? _forwardCompactedPipeline
                    : _forwardPipeline;
                return family switch
                {
                    ForwardOpaquePipelineFamily.Full or
                    ForwardOpaquePipelineFamily.CompactedFull or
                    ForwardOpaquePipelineFamily.Simple or
                    ForwardOpaquePipelineFamily.SimpleFullInput or
                    ForwardOpaquePipelineFamily.CompactedSimple or
                    ForwardOpaquePipelineFamily.CompactedSimpleFullInput =>
                        universalPipeline,
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(family),
                        family,
                        "Unknown forward opaque pipeline family.")
                };
            }

            return family switch
            {
                ForwardOpaquePipelineFamily.Full => _forwardPipeline,
                ForwardOpaquePipelineFamily.CompactedFull =>
                    _forwardCompactedPipeline,
                ForwardOpaquePipelineFamily.Simple => _forwardSimplePipeline,
                ForwardOpaquePipelineFamily.SimpleFullInput =>
                    _forwardSimpleFullInputPipeline,
                ForwardOpaquePipelineFamily.CompactedSimple =>
                    _forwardCompactedSimplePipeline,
                ForwardOpaquePipelineFamily.CompactedSimpleFullInput =>
                    _forwardCompactedSimpleFullInputPipeline,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(family),
                    family,
                    "Unknown forward opaque pipeline family.")
            };
        }

        private ForwardOpaquePipelineFamily
            ResolveEffectiveForwardOpaquePipelineFamily(
                ForwardOpaquePipelineFamily requestedFamily)
        {
            _ = ResolveBasePipeline(requestedFamily);
            if (!RendererBuildConfiguration.FastPipelineStartup)
                return requestedFamily;

            return TasklessSubmissionEnabled
                ? ForwardOpaquePipelineFamily.CompactedFull
                : ForwardOpaquePipelineFamily.Full;
        }

        private void InvalidateForwardOpaquePipelineCache()
        {
            Array.Clear(_forwardOpaquePipelineCache);
            Array.Clear(_forwardOpaquePipelineCacheValid);
            _forwardOpaquePipelineCacheEntryCount = 0;
        }

        /// <summary>
        /// Releases the optional C5 MRT variants during a renderer-controlled
        /// device-idle fallback transition. Ordinary forward pipelines remain
        /// intact and immediately become the sole selectable path.
        /// </summary>
        internal void DisableNearFieldDirectSourceAfterDeviceIdle(string reason)
        {
            InvalidateForwardOpaquePipelineCache();
            DestroyNearFieldDirectSourcePipelines();
            DestroyCombinedAdvancedGiPipelines();
            _nearFieldDirectSourceConfiguration =
                ForwardNearFieldDirectSourcePipelineConfiguration.Disabled;
            NearFieldDirectSourceAttachmentEnabled = false;
            NearFieldDirectSourceFailureReason = string.IsNullOrWhiteSpace(reason)
                ? "near-field-direct-source-disabled"
                : reason;
            CombinedAdvancedGiFailureReason =
                "combined-advanced-GI-C5-disabled";
        }

        internal void DisableGiCausticReceiverAfterDeviceIdle(string reason)
        {
            InvalidateForwardOpaquePipelineCache();
            DestroyGiCausticReceiverPipelines();
            DestroyCombinedAdvancedGiPipelines();
            _giCausticReceiverConfiguration =
                ForwardGiCausticReceiverPipelineConfiguration.Disabled;
            GiCausticReceiverAttachmentEnabled = false;
            GiCausticReceiverFailureReason = string.IsNullOrWhiteSpace(reason)
                ? "caustic-forward-receiver-disabled"
                : reason;
            CombinedAdvancedGiFailureReason =
                "combined-advanced-GI-C4-disabled";
        }

        public VkPipeline ResolveOpaqueSpecializedPipeline(
            VkPipeline exactPipeline,
            bool receiverCacheRequired,
            bool globalIlluminationDisabled,
            bool alphaMaskReceiverFeedbackRequired = false)
        {
            if (alphaMaskReceiverFeedbackRequired && !receiverCacheRequired)
            {
                return ResolveOpaqueVariant(
                    exactPipeline,
                    _forwardAlphaMaskReceiverFeedbackPipeline,
                    _forwardCompactedAlphaMaskReceiverFeedbackPipeline,
                    _forwardSimpleAlphaMaskReceiverFeedbackPipeline,
                    _forwardSimpleFullInputAlphaMaskReceiverFeedbackPipeline,
                    _forwardCompactedSimpleAlphaMaskReceiverFeedbackPipeline,
                    _forwardCompactedSimpleFullInputAlphaMaskReceiverFeedbackPipeline);
            }
            if (MaterialTransportProvenanceAttachmentEnabled)
            {
                return exactPipeline;
            }

            if (globalIlluminationDisabled)
            {
                return ResolveOpaqueVariant(
                    exactPipeline,
                    _forwardGiDisabledPipeline,
                    _forwardCompactedGiDisabledPipeline,
                    _forwardSimpleGiDisabledPipeline,
                    _forwardSimpleFullInputGiDisabledPipeline,
                    _forwardCompactedSimpleGiDisabledPipeline,
                    _forwardCompactedSimpleFullInputGiDisabledPipeline);
            }

            if (!receiverCacheRequired)
                return exactPipeline;

            return ResolveOpaqueVariant(
                exactPipeline,
                _forwardReceiverCachePipeline,
                _forwardCompactedReceiverCachePipeline,
                _forwardSimpleReceiverCachePipeline,
                _forwardSimpleFullInputReceiverCachePipeline,
                _forwardCompactedSimpleReceiverCachePipeline,
                _forwardCompactedSimpleFullInputReceiverCachePipeline);
        }

        /// <summary>
        /// Maps one ordinary opaque/alpha-mask base pipeline to the matching C5
        /// MRT program. When the frame-local DDGI receiver cache is ready, its
        /// cache-required C5 sibling preserves the same SceneColor path used
        /// without SSGI while still writing both C5 producer attachments.
        /// </summary>
        public bool TryResolveNearFieldDirectSourcePipeline(
            VkPipeline exactPipeline,
            bool receiverCacheRequired,
            out VkPipeline nearFieldPipeline)
        {
            nearFieldPipeline = default;
            if (!NearFieldDirectSourceAttachmentEnabled)
                return false;

            if (exactPipeline.Handle == _forwardPipeline.Handle)
            {
                nearFieldPipeline = receiverCacheRequired
                    ? _forwardReceiverCacheNearFieldDirectSourcePipeline
                    : _forwardNearFieldDirectSourcePipeline;
            }
            else if (exactPipeline.Handle == _forwardCompactedPipeline.Handle)
            {
                nearFieldPipeline = receiverCacheRequired
                    ? _forwardCompactedReceiverCacheNearFieldDirectSourcePipeline
                    : _forwardCompactedNearFieldDirectSourcePipeline;
            }
            else if (exactPipeline.Handle == _forwardSimplePipeline.Handle)
            {
                nearFieldPipeline = receiverCacheRequired
                    ? _forwardSimpleReceiverCacheNearFieldDirectSourcePipeline
                    : _forwardSimpleNearFieldDirectSourcePipeline;
            }
            else if (exactPipeline.Handle == _forwardSimpleFullInputPipeline.Handle)
            {
                nearFieldPipeline = receiverCacheRequired
                    ? _forwardSimpleFullInputReceiverCacheNearFieldDirectSourcePipeline
                    : _forwardSimpleFullInputNearFieldDirectSourcePipeline;
            }
            else if (exactPipeline.Handle == _forwardCompactedSimplePipeline.Handle)
            {
                nearFieldPipeline = receiverCacheRequired
                    ? _forwardCompactedSimpleReceiverCacheNearFieldDirectSourcePipeline
                    : _forwardCompactedSimpleNearFieldDirectSourcePipeline;
            }
            else if (exactPipeline.Handle ==
                _forwardCompactedSimpleFullInputPipeline.Handle)
            {
                nearFieldPipeline = receiverCacheRequired
                    ? _forwardCompactedSimpleFullInputReceiverCacheNearFieldDirectSourcePipeline
                    : _forwardCompactedSimpleFullInputNearFieldDirectSourcePipeline;
            }
            else
            {
                return false;
            }

            return nearFieldPipeline.Handle != 0;
        }

        public bool TryResolveGiCausticReceiverPipeline(
            VkPipeline exactPipeline,
            bool receiverCacheRequired,
            out VkPipeline causticPipeline)
        {
            causticPipeline = default;
            if (!GiCausticReceiverAttachmentEnabled)
                return false;

            if (exactPipeline.Handle == _forwardPipeline.Handle)
                causticPipeline = receiverCacheRequired
                    ? _forwardReceiverCacheGiCausticReceiverPipeline
                    : _forwardGiCausticReceiverPipeline;
            else if (exactPipeline.Handle == _forwardCompactedPipeline.Handle)
                causticPipeline = receiverCacheRequired
                    ? _forwardCompactedReceiverCacheGiCausticReceiverPipeline
                    : _forwardCompactedGiCausticReceiverPipeline;
            else if (exactPipeline.Handle == _forwardSimplePipeline.Handle)
                causticPipeline = receiverCacheRequired
                    ? _forwardSimpleReceiverCacheGiCausticReceiverPipeline
                    : _forwardSimpleGiCausticReceiverPipeline;
            else if (exactPipeline.Handle == _forwardSimpleFullInputPipeline.Handle)
                causticPipeline = receiverCacheRequired
                    ? _forwardSimpleFullInputReceiverCacheGiCausticReceiverPipeline
                    : _forwardSimpleFullInputGiCausticReceiverPipeline;
            else if (exactPipeline.Handle == _forwardCompactedSimplePipeline.Handle)
                causticPipeline = receiverCacheRequired
                    ? _forwardCompactedSimpleReceiverCacheGiCausticReceiverPipeline
                    : _forwardCompactedSimpleGiCausticReceiverPipeline;
            else if (exactPipeline.Handle ==
                _forwardCompactedSimpleFullInputPipeline.Handle)
            {
                causticPipeline = receiverCacheRequired
                    ? _forwardCompactedSimpleFullInputReceiverCacheGiCausticReceiverPipeline
                    : _forwardCompactedSimpleFullInputGiCausticReceiverPipeline;
            }
            else
            {
                return false;
            }

            return causticPipeline.Handle != 0;
        }

        /// <summary>
        /// Maps an ordinary opaque/alpha-mask pipeline to the single MRT
        /// program that writes both the C4 and C5 producer contracts.
        /// </summary>
        public bool TryResolveCombinedAdvancedGiPipeline(
            VkPipeline exactPipeline,
            bool receiverCacheRequired,
            out VkPipeline combinedPipeline)
        {
            combinedPipeline = default;
            if (!CombinedAdvancedGiAttachmentEnabled ||
                !NearFieldDirectSourceAttachmentEnabled ||
                !GiCausticReceiverAttachmentEnabled ||
                _nearFieldDirectSourceConfiguration.SourceProducerMode !=
                    SimpleDdgiNearFieldSourceProducerMode.ForwardMrt)
            {
                return false;
            }

            if (exactPipeline.Handle == _forwardPipeline.Handle)
                combinedPipeline = receiverCacheRequired
                    ? _forwardReceiverCacheCombinedAdvancedGiPipeline
                    : _forwardCombinedAdvancedGiPipeline;
            else if (exactPipeline.Handle == _forwardCompactedPipeline.Handle)
                combinedPipeline = receiverCacheRequired
                    ? _forwardCompactedReceiverCacheCombinedAdvancedGiPipeline
                    : _forwardCompactedCombinedAdvancedGiPipeline;
            else if (exactPipeline.Handle == _forwardSimplePipeline.Handle)
                combinedPipeline = receiverCacheRequired
                    ? _forwardSimpleReceiverCacheCombinedAdvancedGiPipeline
                    : _forwardSimpleCombinedAdvancedGiPipeline;
            else if (exactPipeline.Handle == _forwardSimpleFullInputPipeline.Handle)
                combinedPipeline = receiverCacheRequired
                    ? _forwardSimpleFullInputReceiverCacheCombinedAdvancedGiPipeline
                    : _forwardSimpleFullInputCombinedAdvancedGiPipeline;
            else if (exactPipeline.Handle == _forwardCompactedSimplePipeline.Handle)
                combinedPipeline = receiverCacheRequired
                    ? _forwardCompactedSimpleReceiverCacheCombinedAdvancedGiPipeline
                    : _forwardCompactedSimpleCombinedAdvancedGiPipeline;
            else if (exactPipeline.Handle ==
                _forwardCompactedSimpleFullInputPipeline.Handle)
            {
                combinedPipeline = receiverCacheRequired
                    ? _forwardCompactedSimpleFullInputReceiverCacheCombinedAdvancedGiPipeline
                    : _forwardCompactedSimpleFullInputCombinedAdvancedGiPipeline;
            }
            else
            {
                return false;
            }

            return combinedPipeline.Handle != 0;
        }

        /// <summary>
        /// Commits an extent-bound source contract for a replacement C5
        /// generation. Graphics pipeline binaries are extent-independent, so
        /// their already validated MRT variants remain reusable.
        /// </summary>
        internal void PublishNearFieldDirectSourceGeneration(
            in ForwardNearFieldDirectSourcePipelineConfiguration configuration)
        {
            if (!ForwardNearFieldDirectSourceContract
                    .TryValidatePipelineConfiguration(
                        configuration,
                        out string failure))
            {
                throw new InvalidOperationException(failure);
            }
            if (!NearFieldDirectSourceAttachmentEnabled)
            {
                throw new InvalidOperationException(
                    "C5 source pipeline variants are unavailable for generation publication.");
            }

            _nearFieldDirectSourceConfiguration = configuration;
            NearFieldDirectSourceFailureReason = "valid";
        }

        private bool TryEnsureNearFieldDirectSourcePipeline(
            VkPipeline exactPipeline,
            bool receiverCacheRequired)
        {
            bool traceResolutionSource =
                _nearFieldDirectSourceConfiguration.SourceProducerMode ==
                SimpleDdgiNearFieldSourceProducerMode.TraceResolutionRaster;
            if (traceResolutionSource && receiverCacheRequired)
                return false;

            string fullFragment = traceResolutionSource
                ? ForwardNearFieldDirectSourceContract
                    .TraceResolutionOpaqueFragmentShader
                : receiverCacheRequired
                ? ForwardNearFieldDirectSourceContract
                    .ReceiverCacheOpaqueFragmentShader
                : ForwardNearFieldDirectSourceContract.OpaqueFragmentShader;
            string simpleFragment = traceResolutionSource
                ? ForwardNearFieldDirectSourceContract
                    .TraceResolutionSimpleOpaqueFragmentShader
                : receiverCacheRequired
                ? ForwardNearFieldDirectSourceContract
                    .ReceiverCacheSimpleOpaqueFragmentShader
                : ForwardNearFieldDirectSourceContract.SimpleOpaqueFragmentShader;
            string simpleFullInputFragment = traceResolutionSource
                ? ForwardNearFieldDirectSourceContract
                    .TraceResolutionSimpleFullInputOpaqueFragmentShader
                : receiverCacheRequired
                ? ForwardNearFieldDirectSourceContract
                    .ReceiverCacheSimpleFullInputOpaqueFragmentShader
                : ForwardNearFieldDirectSourceContract
                    .SimpleFullInputOpaqueFragmentShader;

            if (receiverCacheRequired)
            {
                return
                    TryEnsureAdvancedGiPipeline(
                        exactPipeline,
                        _forwardPipeline,
                        ref _forwardReceiverCacheNearFieldDirectSourcePipeline,
                        _forwardTaskShaderName,
                        "forward.mesh.spv",
                        fullFragment,
                        "C5 receiver-cache full",
                        AdvancedGiPipelineKind.NearField) &&
                    TryEnsureAdvancedGiPipeline(
                        exactPipeline,
                        _forwardCompactedPipeline,
                        ref _forwardCompactedReceiverCacheNearFieldDirectSourcePipeline,
                        null,
                        _compactedForwardMeshShaderName,
                        fullFragment,
                        "C5 receiver-cache compacted full",
                        AdvancedGiPipelineKind.NearField) &&
                    TryEnsureAdvancedGiPipeline(
                        exactPipeline,
                        _forwardSimplePipeline,
                        ref _forwardSimpleReceiverCacheNearFieldDirectSourcePipeline,
                        _forwardTaskShaderName,
                        "forward_simple.mesh.spv",
                        simpleFragment,
                        "C5 receiver-cache simple",
                        AdvancedGiPipelineKind.NearField) &&
                    TryEnsureAdvancedGiPipeline(
                        exactPipeline,
                        _forwardSimpleFullInputPipeline,
                        ref _forwardSimpleFullInputReceiverCacheNearFieldDirectSourcePipeline,
                        _forwardTaskShaderName,
                        "forward.mesh.spv",
                        simpleFullInputFragment,
                        "C5 receiver-cache simple full-input",
                        AdvancedGiPipelineKind.NearField) &&
                    TryEnsureAdvancedGiPipeline(
                        exactPipeline,
                        _forwardCompactedSimplePipeline,
                        ref _forwardCompactedSimpleReceiverCacheNearFieldDirectSourcePipeline,
                        null,
                        _compactedForwardSimpleMeshShaderName,
                        simpleFragment,
                        "C5 receiver-cache compacted simple",
                        AdvancedGiPipelineKind.NearField) &&
                    TryEnsureAdvancedGiPipeline(
                        exactPipeline,
                        _forwardCompactedSimpleFullInputPipeline,
                        ref _forwardCompactedSimpleFullInputReceiverCacheNearFieldDirectSourcePipeline,
                        null,
                        _compactedForwardMeshShaderName,
                        simpleFullInputFragment,
                        "C5 receiver-cache compacted simple full-input",
                        AdvancedGiPipelineKind.NearField);
            }

            return
                TryEnsureAdvancedGiPipeline(
                    exactPipeline,
                    _forwardPipeline,
                    ref _forwardNearFieldDirectSourcePipeline,
                    _forwardTaskShaderName,
                    "forward.mesh.spv",
                    fullFragment,
                    "C5 full",
                    AdvancedGiPipelineKind.NearField) &&
                TryEnsureAdvancedGiPipeline(
                    exactPipeline,
                    _forwardCompactedPipeline,
                    ref _forwardCompactedNearFieldDirectSourcePipeline,
                    null,
                    _compactedForwardMeshShaderName,
                    fullFragment,
                    "C5 compacted full",
                    AdvancedGiPipelineKind.NearField) &&
                TryEnsureAdvancedGiPipeline(
                    exactPipeline,
                    _forwardSimplePipeline,
                    ref _forwardSimpleNearFieldDirectSourcePipeline,
                    _forwardTaskShaderName,
                    "forward_simple.mesh.spv",
                    simpleFragment,
                    "C5 simple",
                    AdvancedGiPipelineKind.NearField) &&
                TryEnsureAdvancedGiPipeline(
                    exactPipeline,
                    _forwardSimpleFullInputPipeline,
                    ref _forwardSimpleFullInputNearFieldDirectSourcePipeline,
                    _forwardTaskShaderName,
                    "forward.mesh.spv",
                    simpleFullInputFragment,
                    "C5 simple full-input",
                    AdvancedGiPipelineKind.NearField) &&
                TryEnsureAdvancedGiPipeline(
                    exactPipeline,
                    _forwardCompactedSimplePipeline,
                    ref _forwardCompactedSimpleNearFieldDirectSourcePipeline,
                    null,
                    _compactedForwardSimpleMeshShaderName,
                    simpleFragment,
                    "C5 compacted simple",
                    AdvancedGiPipelineKind.NearField) &&
                TryEnsureAdvancedGiPipeline(
                    exactPipeline,
                    _forwardCompactedSimpleFullInputPipeline,
                    ref _forwardCompactedSimpleFullInputNearFieldDirectSourcePipeline,
                    null,
                    _compactedForwardMeshShaderName,
                    simpleFullInputFragment,
                    "C5 compacted simple full-input",
                    AdvancedGiPipelineKind.NearField);
        }

        private bool TryEnsureGiCausticReceiverPipeline(
            VkPipeline exactPipeline,
            bool receiverCacheRequired)
        {
            if (receiverCacheRequired)
            {
                return
                    TryEnsureAdvancedGiPipeline(
                        exactPipeline,
                        _forwardPipeline,
                        ref _forwardReceiverCacheGiCausticReceiverPipeline,
                        _forwardTaskShaderName,
                        "forward.mesh.spv",
                        ForwardGiCausticReceiverContract
                            .ReceiverCacheOpaqueFragmentShader,
                        "C4 receiver-cache full",
                        AdvancedGiPipelineKind.Caustic) &&
                    TryEnsureAdvancedGiPipeline(
                        exactPipeline,
                        _forwardCompactedPipeline,
                        ref _forwardCompactedReceiverCacheGiCausticReceiverPipeline,
                        null,
                        _compactedForwardMeshShaderName,
                        ForwardGiCausticReceiverContract
                            .ReceiverCacheOpaqueFragmentShader,
                        "C4 receiver-cache compacted full",
                        AdvancedGiPipelineKind.Caustic) &&
                    TryEnsureAdvancedGiPipeline(
                        exactPipeline,
                        _forwardSimplePipeline,
                        ref _forwardSimpleReceiverCacheGiCausticReceiverPipeline,
                        _forwardTaskShaderName,
                        "forward_simple.mesh.spv",
                        ForwardGiCausticReceiverContract
                            .ReceiverCacheSimpleOpaqueFragmentShader,
                        "C4 receiver-cache simple",
                        AdvancedGiPipelineKind.Caustic) &&
                    TryEnsureAdvancedGiPipeline(
                        exactPipeline,
                        _forwardSimpleFullInputPipeline,
                        ref _forwardSimpleFullInputReceiverCacheGiCausticReceiverPipeline,
                        _forwardTaskShaderName,
                        "forward.mesh.spv",
                        ForwardGiCausticReceiverContract
                            .ReceiverCacheSimpleFullInputOpaqueFragmentShader,
                        "C4 receiver-cache simple full-input",
                        AdvancedGiPipelineKind.Caustic) &&
                    TryEnsureAdvancedGiPipeline(
                        exactPipeline,
                        _forwardCompactedSimplePipeline,
                        ref _forwardCompactedSimpleReceiverCacheGiCausticReceiverPipeline,
                        null,
                        _compactedForwardSimpleMeshShaderName,
                        ForwardGiCausticReceiverContract
                            .ReceiverCacheSimpleOpaqueFragmentShader,
                        "C4 receiver-cache compacted simple",
                        AdvancedGiPipelineKind.Caustic) &&
                    TryEnsureAdvancedGiPipeline(
                        exactPipeline,
                        _forwardCompactedSimpleFullInputPipeline,
                        ref _forwardCompactedSimpleFullInputReceiverCacheGiCausticReceiverPipeline,
                        null,
                        _compactedForwardMeshShaderName,
                        ForwardGiCausticReceiverContract
                            .ReceiverCacheSimpleFullInputOpaqueFragmentShader,
                        "C4 receiver-cache compacted simple full-input",
                        AdvancedGiPipelineKind.Caustic);
            }

            return
                TryEnsureAdvancedGiPipeline(
                    exactPipeline,
                    _forwardPipeline,
                    ref _forwardGiCausticReceiverPipeline,
                    _forwardTaskShaderName,
                    "forward.mesh.spv",
                    ForwardGiCausticReceiverContract.OpaqueFragmentShader,
                    "C4 full",
                    AdvancedGiPipelineKind.Caustic) &&
                TryEnsureAdvancedGiPipeline(
                    exactPipeline,
                    _forwardCompactedPipeline,
                    ref _forwardCompactedGiCausticReceiverPipeline,
                    null,
                    _compactedForwardMeshShaderName,
                    ForwardGiCausticReceiverContract.OpaqueFragmentShader,
                    "C4 compacted full",
                    AdvancedGiPipelineKind.Caustic) &&
                TryEnsureAdvancedGiPipeline(
                    exactPipeline,
                    _forwardSimplePipeline,
                    ref _forwardSimpleGiCausticReceiverPipeline,
                    _forwardTaskShaderName,
                    "forward_simple.mesh.spv",
                    ForwardGiCausticReceiverContract.SimpleOpaqueFragmentShader,
                    "C4 simple",
                    AdvancedGiPipelineKind.Caustic) &&
                TryEnsureAdvancedGiPipeline(
                    exactPipeline,
                    _forwardSimpleFullInputPipeline,
                    ref _forwardSimpleFullInputGiCausticReceiverPipeline,
                    _forwardTaskShaderName,
                    "forward.mesh.spv",
                    ForwardGiCausticReceiverContract
                        .SimpleFullInputOpaqueFragmentShader,
                    "C4 simple full-input",
                    AdvancedGiPipelineKind.Caustic) &&
                TryEnsureAdvancedGiPipeline(
                    exactPipeline,
                    _forwardCompactedSimplePipeline,
                    ref _forwardCompactedSimpleGiCausticReceiverPipeline,
                    null,
                    _compactedForwardSimpleMeshShaderName,
                    ForwardGiCausticReceiverContract.SimpleOpaqueFragmentShader,
                    "C4 compacted simple",
                    AdvancedGiPipelineKind.Caustic) &&
                TryEnsureAdvancedGiPipeline(
                    exactPipeline,
                    _forwardCompactedSimpleFullInputPipeline,
                    ref _forwardCompactedSimpleFullInputGiCausticReceiverPipeline,
                    null,
                    _compactedForwardMeshShaderName,
                    ForwardGiCausticReceiverContract
                        .SimpleFullInputOpaqueFragmentShader,
                    "C4 compacted simple full-input",
                    AdvancedGiPipelineKind.Caustic);
        }

        private bool TryEnsureCombinedAdvancedGiPipeline(
            VkPipeline exactPipeline,
            bool receiverCacheRequired)
        {
            if (receiverCacheRequired)
            {
                return
                    TryEnsureAdvancedGiPipeline(
                        exactPipeline,
                        _forwardPipeline,
                        ref _forwardReceiverCacheCombinedAdvancedGiPipeline,
                        _forwardTaskShaderName,
                        "forward.mesh.spv",
                        ForwardAdvancedGiCombinedContract
                            .ReceiverCacheOpaqueFragmentShader,
                        "combined C4/C5 receiver-cache full",
                        AdvancedGiPipelineKind.Combined) &&
                    TryEnsureAdvancedGiPipeline(
                        exactPipeline,
                        _forwardCompactedPipeline,
                        ref _forwardCompactedReceiverCacheCombinedAdvancedGiPipeline,
                        null,
                        _compactedForwardMeshShaderName,
                        ForwardAdvancedGiCombinedContract
                            .ReceiverCacheOpaqueFragmentShader,
                        "combined C4/C5 receiver-cache compacted full",
                        AdvancedGiPipelineKind.Combined) &&
                    TryEnsureAdvancedGiPipeline(
                        exactPipeline,
                        _forwardSimplePipeline,
                        ref _forwardSimpleReceiverCacheCombinedAdvancedGiPipeline,
                        _forwardTaskShaderName,
                        "forward_simple.mesh.spv",
                        ForwardAdvancedGiCombinedContract
                            .ReceiverCacheSimpleOpaqueFragmentShader,
                        "combined C4/C5 receiver-cache simple",
                        AdvancedGiPipelineKind.Combined) &&
                    TryEnsureAdvancedGiPipeline(
                        exactPipeline,
                        _forwardSimpleFullInputPipeline,
                        ref _forwardSimpleFullInputReceiverCacheCombinedAdvancedGiPipeline,
                        _forwardTaskShaderName,
                        "forward.mesh.spv",
                        ForwardAdvancedGiCombinedContract
                            .ReceiverCacheSimpleFullInputOpaqueFragmentShader,
                        "combined C4/C5 receiver-cache simple full-input",
                        AdvancedGiPipelineKind.Combined) &&
                    TryEnsureAdvancedGiPipeline(
                        exactPipeline,
                        _forwardCompactedSimplePipeline,
                        ref _forwardCompactedSimpleReceiverCacheCombinedAdvancedGiPipeline,
                        null,
                        _compactedForwardSimpleMeshShaderName,
                        ForwardAdvancedGiCombinedContract
                            .ReceiverCacheSimpleOpaqueFragmentShader,
                        "combined C4/C5 receiver-cache compacted simple",
                        AdvancedGiPipelineKind.Combined) &&
                    TryEnsureAdvancedGiPipeline(
                        exactPipeline,
                        _forwardCompactedSimpleFullInputPipeline,
                        ref _forwardCompactedSimpleFullInputReceiverCacheCombinedAdvancedGiPipeline,
                        null,
                        _compactedForwardMeshShaderName,
                        ForwardAdvancedGiCombinedContract
                            .ReceiverCacheSimpleFullInputOpaqueFragmentShader,
                        "combined C4/C5 receiver-cache compacted simple full-input",
                        AdvancedGiPipelineKind.Combined);
            }

            return
                TryEnsureAdvancedGiPipeline(
                    exactPipeline,
                    _forwardPipeline,
                    ref _forwardCombinedAdvancedGiPipeline,
                    _forwardTaskShaderName,
                    "forward.mesh.spv",
                    ForwardAdvancedGiCombinedContract.OpaqueFragmentShader,
                    "combined C4/C5 full",
                    AdvancedGiPipelineKind.Combined) &&
                TryEnsureAdvancedGiPipeline(
                    exactPipeline,
                    _forwardCompactedPipeline,
                    ref _forwardCompactedCombinedAdvancedGiPipeline,
                    null,
                    _compactedForwardMeshShaderName,
                    ForwardAdvancedGiCombinedContract.OpaqueFragmentShader,
                    "combined C4/C5 compacted full",
                    AdvancedGiPipelineKind.Combined) &&
                TryEnsureAdvancedGiPipeline(
                    exactPipeline,
                    _forwardSimplePipeline,
                    ref _forwardSimpleCombinedAdvancedGiPipeline,
                    _forwardTaskShaderName,
                    "forward_simple.mesh.spv",
                    ForwardAdvancedGiCombinedContract.SimpleOpaqueFragmentShader,
                    "combined C4/C5 simple",
                    AdvancedGiPipelineKind.Combined) &&
                TryEnsureAdvancedGiPipeline(
                    exactPipeline,
                    _forwardSimpleFullInputPipeline,
                    ref _forwardSimpleFullInputCombinedAdvancedGiPipeline,
                    _forwardTaskShaderName,
                    "forward.mesh.spv",
                    ForwardAdvancedGiCombinedContract
                        .SimpleFullInputOpaqueFragmentShader,
                    "combined C4/C5 simple full-input",
                    AdvancedGiPipelineKind.Combined) &&
                TryEnsureAdvancedGiPipeline(
                    exactPipeline,
                    _forwardCompactedSimplePipeline,
                    ref _forwardCompactedSimpleCombinedAdvancedGiPipeline,
                    null,
                    _compactedForwardSimpleMeshShaderName,
                    ForwardAdvancedGiCombinedContract.SimpleOpaqueFragmentShader,
                    "combined C4/C5 compacted simple",
                    AdvancedGiPipelineKind.Combined) &&
                TryEnsureAdvancedGiPipeline(
                    exactPipeline,
                    _forwardCompactedSimpleFullInputPipeline,
                    ref _forwardCompactedSimpleFullInputCombinedAdvancedGiPipeline,
                    null,
                    _compactedForwardMeshShaderName,
                    ForwardAdvancedGiCombinedContract
                        .SimpleFullInputOpaqueFragmentShader,
                    "combined C4/C5 compacted simple full-input",
                    AdvancedGiPipelineKind.Combined);
        }

        private bool TryEnsureAdvancedGiPipeline(
            VkPipeline exactPipeline,
            VkPipeline matchingBasePipeline,
            ref VkPipeline specializedPipeline,
            string? taskShaderName,
            string meshShaderName,
            string fragmentShaderName,
            string debugVariantName,
            AdvancedGiPipelineKind kind)
        {
            if (exactPipeline.Handle != matchingBasePipeline.Handle ||
                specializedPipeline.Handle != 0)
            {
                return true;
            }

            try
            {
                bool traceResolutionNearField =
                    kind == AdvancedGiPipelineKind.NearField &&
                    _nearFieldDirectSourceConfiguration.SourceProducerMode ==
                    SimpleDdgiNearFieldSourceProducerMode.TraceResolutionRaster;
                specializedPipeline = CreateGraphicsPipeline(
                    taskShaderName,
                    meshShaderName,
                    fragmentShaderName,
                    traceResolutionNearField
                        ? ForwardNearFieldDirectSourceContract
                            .RequiredAttachmentFormat
                        : _colorFormat,
                    _depthFormat,
                    hasColorAttachment: true,
                    depthWriteEnable: traceResolutionNearField,
                    blendEnable: false,
                    cullMode: CullModeFlags.None,
                    depthBiasEnable: false,
                    secondaryColorFormat: kind == AdvancedGiPipelineKind.NearField
                        ? traceResolutionNearField
                            ? ForwardNearFieldDirectSourceContract
                                .ReceiverPayloadFormat
                            : ForwardNearFieldDirectSourceContract
                                .RequiredAttachmentFormat
                        : ForwardGiCausticReceiverContract.ReceiverPayloadFormat,
                    tertiaryColorFormat: kind switch
                    {
                        AdvancedGiPipelineKind.NearField when
                            !traceResolutionNearField =>
                            ForwardNearFieldDirectSourceContract
                                .ReceiverPayloadFormat,
                        AdvancedGiPipelineKind.Combined =>
                            ForwardNearFieldDirectSourceContract
                                .RequiredAttachmentFormat,
                        _ => null
                    },
                    quaternaryColorFormat: kind == AdvancedGiPipelineKind.Combined
                        ? ForwardNearFieldDirectSourceContract
                            .ReceiverPayloadFormat
                        : null);
                _context.SetDebugName(
                    specializedPipeline.Handle,
                    ObjectType.Pipeline,
                    $"Deferred {debugVariantName} Mesh Pipeline");
                return true;
            }
            catch (Exception ex)
            {
                string reason =
                    "deferred-advanced-GI-pipeline-creation-failed:" +
                    ex.GetType().Name + ":" + ex.Message;
                switch (kind)
                {
                    case AdvancedGiPipelineKind.NearField:
                        NearFieldDirectSourceFailureReason = reason;
                        break;
                    case AdvancedGiPipelineKind.Caustic:
                        GiCausticReceiverFailureReason = reason;
                        break;
                    case AdvancedGiPipelineKind.Combined:
                        CombinedAdvancedGiFailureReason = reason;
                        break;
                }
                return false;
            }
        }

        private enum AdvancedGiPipelineKind
        {
            NearField,
            Caustic,
            Combined
        }

        private VkPipeline ResolveOpaqueVariant(
            VkPipeline exactPipeline,
            VkPipeline fullPipeline,
            VkPipeline compactedPipeline,
            VkPipeline simplePipeline,
            VkPipeline simpleFullInputPipeline,
            VkPipeline compactedSimplePipeline,
            VkPipeline compactedSimpleFullInputPipeline)
        {
            if (exactPipeline.Handle == _forwardPipeline.Handle)
                return ResolveAvailableSpecializedPipeline(
                    fullPipeline,
                    exactPipeline);
            if (exactPipeline.Handle == _forwardCompactedPipeline.Handle)
                return ResolveAvailableSpecializedPipeline(
                    compactedPipeline,
                    exactPipeline);
            if (exactPipeline.Handle == _forwardSimplePipeline.Handle)
                return ResolveAvailableSpecializedPipeline(
                    simplePipeline,
                    exactPipeline);
            if (exactPipeline.Handle == _forwardSimpleFullInputPipeline.Handle)
                return ResolveAvailableSpecializedPipeline(
                    simpleFullInputPipeline,
                    exactPipeline);
            if (exactPipeline.Handle == _forwardCompactedSimplePipeline.Handle)
                return ResolveAvailableSpecializedPipeline(
                    compactedSimplePipeline,
                    exactPipeline);
            if (exactPipeline.Handle ==
                _forwardCompactedSimpleFullInputPipeline.Handle)
            {
                return ResolveAvailableSpecializedPipeline(
                    compactedSimpleFullInputPipeline,
                    exactPipeline);
            }

            return exactPipeline;
        }

        private static VkPipeline ResolveAvailableSpecializedPipeline(
            VkPipeline specializedPipeline,
            VkPipeline exactPipeline)
        {
            // The zero-handle guard keeps this resolver fail-safe when the
            // Development tier deliberately omits an optional specialization.
            return specializedPipeline.Handle != 0
                ? specializedPipeline
                : exactPipeline;
        }

        public void Recreate(Format colorFormat, Format depthFormat)
        {
            DestroyPipelines();
            CreatePipelines(colorFormat, depthFormat);
            CreateComputePipelines();
        }

        private void ValidatePushConstantRange(uint requiredSize)
        {
            var properties = new PhysicalDeviceProperties();
            _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice, &properties);

            if (requiredSize > properties.Limits.MaxPushConstantsSize)
            {
                throw new VulkanException(
                    $"GPU supports {properties.Limits.MaxPushConstantsSize} bytes of push constants, " +
                    $"but mesh rendering requires {requiredSize} bytes.");
            }
        }

        private void CreatePipelineCache()
        {
            if (_pipelineCacheService != null)
            {
                _pipelineCache = _pipelineCacheService.Cache;
                return;
            }

            var cacheInfo = new PipelineCacheCreateInfo
            {
                SType = StructureType.PipelineCacheCreateInfo
            };

            Result result = _context.Api.CreatePipelineCache(
                _context.Device,
                &cacheInfo,
                null,
                out _pipelineCache);

            if (result != Result.Success)
                throw new VulkanException("Failed to create mesh pipeline cache", result);
            _context.SetDebugName(_pipelineCache.Handle, ObjectType.PipelineCache, "Mesh Pipeline Cache");
        }

        private void CreateForwardReceiverCacheBufferSetLayout()
        {
            DescriptorSetLayoutBinding* bindings =
                stackalloc DescriptorSetLayoutBinding[2];
            bindings[0] = new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit
            };
            bindings[1] = bindings[0];
            bindings[1].Binding = 1;
            var info = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 2,
                PBindings = bindings
            };
            Result result = _context.Api.CreateDescriptorSetLayout(
                _context.Device,
                &info,
                null,
                out _forwardReceiverCacheBufferSetLayout);
            if (result != Result.Success)
            {
                throw new VulkanException(
                    "Failed to create forward receiver-cache buffer descriptor layout",
                    result);
            }
            _context.SetDebugName(
                _forwardReceiverCacheBufferSetLayout.Handle,
                ObjectType.DescriptorSetLayout,
                "Forward Receiver Cache Buffer Descriptor Layout");
        }

        private void CreatePipelineLayout()
        {
            var setLayouts = stackalloc DescriptorSetLayout[3];
            setLayouts[0] = _bindlessHeap.StorageBufferSetLayout;
            setLayouts[1] = _bindlessHeap.TextureSamplerSetLayout;
            setLayouts[2] = _forwardReceiverCacheBufferSetLayout;

            var pushConstantRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.TaskBitExt | ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit,
                Offset = 0,
                Size = (uint)Math.Max(
                    Math.Max(Marshal.SizeOf<GPUDepthPushConstants>(), Marshal.SizeOf<GPUForwardPushConstants>()),
                    Marshal.SizeOf<GPUMotionVectorPushConstants>())
            };

            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 3,
                PSetLayouts = setLayouts,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushConstantRange
            };

            Result result = _context.Api.CreatePipelineLayout(
                _context.Device,
                &layoutInfo,
                null,
                out _layout);

            if (result != Result.Success)
                throw new VulkanException("Failed to create mesh pipeline layout", result);
            _context.SetDebugName(_layout.Handle, ObjectType.PipelineLayout, "Mesh Pipeline Layout");
        }

        private void CreateRayTransparentPipelineLayout()
        {
            if (_raySceneDescriptors?.IsAvailable != true)
            {
                RayTransparentPipelineFailureReason =
                    _raySceneDescriptors?.FailureDetail ??
                    "the shared ray-scene descriptor bank is unavailable";
                return;
            }

            var setLayouts = stackalloc DescriptorSetLayout[3];
            setLayouts[0] = _bindlessHeap.StorageBufferSetLayout;
            setLayouts[1] = _bindlessHeap.TextureSamplerSetLayout;
            setLayouts[2] = _raySceneDescriptors.Layout;
            var pushConstantRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.TaskBitExt |
                    ShaderStageFlags.MeshBitExt |
                    ShaderStageFlags.FragmentBit,
                Offset = 0,
                Size = (uint)Math.Max(
                    Math.Max(
                        Marshal.SizeOf<GPUDepthPushConstants>(),
                        Marshal.SizeOf<GPUForwardPushConstants>()),
                    Marshal.SizeOf<GPUMotionVectorPushConstants>())
            };
            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 3,
                PSetLayouts = setLayouts,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushConstantRange
            };
            Result result = _context.Api.CreatePipelineLayout(
                _context.Device,
                &layoutInfo,
                null,
                out _rayTransparentLayout);
            if (result != Result.Success)
            {
                throw new VulkanException(
                    "Failed to create the ray-query transparent mesh pipeline layout",
                    result);
            }
            _context.SetDebugName(
                _rayTransparentLayout.Handle,
                ObjectType.PipelineLayout,
                "Ray Query Transparent Mesh Pipeline Layout");
        }

        private void CreateSceneSubmissionComputeLayout()
        {
            var setLayouts = stackalloc DescriptorSetLayout[2];
            setLayouts[0] = _bindlessHeap.StorageBufferSetLayout;
            setLayouts[1] = _bindlessHeap.TextureSamplerSetLayout;

            var pushConstantRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.ComputeBit,
                Offset = 0,
                Size = (uint)Math.Max(
                    Marshal.SizeOf<GPUSceneOpaqueCompactionPushConstants>(),
                    Marshal.SizeOf<GPUForwardVisibilityCompactionPushConstants>())
            };

            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 2,
                PSetLayouts = setLayouts,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushConstantRange
            };

            Result result = _context.Api.CreatePipelineLayout(
                _context.Device,
                &layoutInfo,
                null,
                out _sceneSubmissionComputeLayout);

            if (result != Result.Success)
                throw new VulkanException("Failed to create scene submission compute pipeline layout", result);
            _context.SetDebugName(_sceneSubmissionComputeLayout.Handle, ObjectType.PipelineLayout, "Scene Submission Compute Pipeline Layout");
        }

        private void CreatePipelines(Format colorFormat, Format depthFormat)
        {
            _colorFormat = colorFormat;
            _depthFormat = depthFormat;
            GpuMeshletCountersEnabled = Settings.Diagnostics.GpuMeshletCountersEnabled;
            string depthTaskShaderName = GpuMeshletCountersEnabled
                ? "depth_diagnostics.task.spv"
                : "depth.task.spv";
            string forwardTaskShaderName = GpuMeshletCountersEnabled
                ? "forward_diagnostics.task.spv"
                : "forward.task.spv";
            _forwardTaskShaderName = forwardTaskShaderName;
            bool materialTransportProvenanceEnabled =
                Settings.GlobalIllumination.DebugView ==
                GlobalIlluminationDebugView.MaterialTransportHitProvenance;
            MaterialTransportProvenanceAttachmentEnabled =
                materialTransportProvenanceEnabled;
            NearFieldDirectSourceAttachmentEnabled = false;
            NearFieldDirectSourceFailureReason =
                "near-field-direct-source-disabled";
            GiCausticReceiverAttachmentEnabled = false;
            GiCausticReceiverFailureReason =
                "caustic-forward-receiver-disabled";
            CombinedAdvancedGiAttachmentEnabled = false;
            CombinedAdvancedGiFailureReason =
                "combined-advanced-GI-disabled";
            HybridReflectionAttachmentEnabled = false;
            HybridReflectionFailureReason =
                "hybrid-reflection-receiver-disabled";
            string provenanceSuffix =
                materialTransportProvenanceEnabled ? "_provenance" : string.Empty;
            string forwardOpaqueFragmentShaderName =
                $"forward_opaque_ddgi{provenanceSuffix}.frag.spv";
            string forwardOpaqueSimpleFragmentShaderName =
                $"forward_opaque_simple_ddgi{provenanceSuffix}.frag.spv";
            string forwardOpaqueSimpleFullInputFragmentShaderName =
                $"forward_opaque_simple_full_input_ddgi{provenanceSuffix}.frag.spv";
            Format? materialTransportProvenanceFormat =
                materialTransportProvenanceEnabled
                    ? RenderTargetManager.MaterialTransportProvenanceFormat
                    : null;
            _materialTransportProvenanceFormat =
                materialTransportProvenanceFormat;

            RunPipelineCreationBatch(
                ("mesh.depth", () =>
                {
                    _depthPipeline = CreateGraphicsPipeline(
                        depthTaskShaderName, "depth.mesh.spv",
                        "depth_sided.frag.spv", colorFormat, depthFormat,
                        false, true, false, CullModeFlags.None, false);
                    _context.SetDebugName(_depthPipeline.Handle,
                        ObjectType.Pipeline, "Depth Prepass Mesh Pipeline");
                }),
                ("mesh.depth.masked", () =>
                {
                    _maskedDepthPipeline = CreateGraphicsPipeline(
                        depthTaskShaderName, "depth_alpha.mesh.spv",
                        "depth_alpha.frag.spv", colorFormat, depthFormat,
                        false, true, false, CullModeFlags.None, false);
                    _context.SetDebugName(_maskedDepthPipeline.Handle,
                        ObjectType.Pipeline,
                        "Masked Depth Alpha-Test Mesh Pipeline");
                }),
                ("mesh.depth.compacted", () =>
                {
                    _compactedDepthPipeline = CreateGraphicsPipeline(
                        null, _compactedDepthMeshShaderName, null,
                        colorFormat, depthFormat, false, true, false,
                        CullModeFlags.None, false);
                    _context.SetDebugName(_compactedDepthPipeline.Handle,
                        ObjectType.Pipeline,
                        "Compacted Mesh-Only Depth Prepass Pipeline");
                }),
                ("mesh.depth.compacted-masked", () =>
                {
                    _compactedMaskedDepthPipeline = CreateGraphicsPipeline(
                        null, _compactedDepthAlphaMeshShaderName,
                        "depth_alpha.frag.spv", colorFormat, depthFormat,
                        false, true, false, CullModeFlags.None, false);
                    _context.SetDebugName(_compactedMaskedDepthPipeline.Handle,
                        ObjectType.Pipeline,
                        "Compacted Mesh-Only Masked Depth Prepass Pipeline");
                }),
                ("mesh.shadow", () =>
                {
                    _shadowDepthPipeline = CreateGraphicsPipeline(
                        "shadow_depth.task.spv", "shadow_depth.mesh.spv", null,
                        colorFormat, Format.D32Sfloat, false, true, false,
                        CullModeFlags.BackBit, true);
                    _context.SetDebugName(_shadowDepthPipeline.Handle,
                        ObjectType.Pipeline,
                        "Directional Shadow Mesh Pipeline");
                }),
                ("mesh.shadow.masked", () =>
                {
                    _shadowAlphaDepthPipeline = CreateGraphicsPipeline(
                        "shadow_depth.task.spv", "shadow_depth_alpha.mesh.spv",
                        "depth_alpha.frag.spv", colorFormat, Format.D32Sfloat,
                        false, true, false, CullModeFlags.None, true);
                    _context.SetDebugName(_shadowAlphaDepthPipeline.Handle,
                        ObjectType.Pipeline, "Alpha-Test Shadow Mesh Pipeline");
                }),
                ("mesh.shadow.compacted-masked", () =>
                {
                    _compactedShadowAlphaDepthPipeline = CreateGraphicsPipeline(
                        null, _compactedShadowAlphaMeshShaderName,
                        "depth_alpha.frag.spv", colorFormat, Format.D32Sfloat,
                        false, true, false, CullModeFlags.None, true);
                    _context.SetDebugName(
                        _compactedShadowAlphaDepthPipeline.Handle,
                        ObjectType.Pipeline,
                        "Compacted Mesh-Only Alpha-Test Shadow Pipeline");
                }));

            if (RendererBuildConfiguration.FastPipelineStartup)
            {
                if (TasklessSubmissionEnabled)
                {
                    _forwardCompactedPipeline = CreateGraphicsPipeline(
                        null, _compactedForwardMeshShaderName,
                        forwardOpaqueFragmentShaderName, colorFormat,
                        depthFormat, true, false, false, CullModeFlags.None,
                        false, materialTransportProvenanceFormat:
                        materialTransportProvenanceFormat);
                    _context.SetDebugName(_forwardCompactedPipeline.Handle,
                        ObjectType.Pipeline,
                        "First-Frame Universal Compacted Opaque Forward Pipeline");
                }
                else
                {
                    _forwardPipeline = CreateGraphicsPipeline(
                        forwardTaskShaderName, "forward.mesh.spv",
                        forwardOpaqueFragmentShaderName, colorFormat,
                        depthFormat, true, false, false, CullModeFlags.None,
                        false, materialTransportProvenanceFormat:
                        materialTransportProvenanceFormat);
                    _context.SetDebugName(_forwardPipeline.Handle,
                        ObjectType.Pipeline,
                        "First-Frame Universal Opaque Forward Pipeline");
                }
            }
            else
            {
                RunPipelineCreationBatch(
                    ("mesh.forward.full", () =>
                    {
                        _forwardPipeline = CreateGraphicsPipeline(
                            forwardTaskShaderName, "forward.mesh.spv",
                            forwardOpaqueFragmentShaderName, colorFormat,
                            depthFormat, true, false, false, CullModeFlags.None,
                            false, materialTransportProvenanceFormat:
                            materialTransportProvenanceFormat);
                        _context.SetDebugName(_forwardPipeline.Handle,
                            ObjectType.Pipeline,
                            "Opaque Forward Plus Mesh Pipeline");
                    }),
                    ("mesh.forward.compacted-full", () =>
                    {
                        _forwardCompactedPipeline = CreateGraphicsPipeline(
                            null, _compactedForwardMeshShaderName,
                            forwardOpaqueFragmentShaderName, colorFormat,
                            depthFormat, true, false, false, CullModeFlags.None,
                            false, materialTransportProvenanceFormat:
                            materialTransportProvenanceFormat);
                        _context.SetDebugName(_forwardCompactedPipeline.Handle,
                            ObjectType.Pipeline,
                            "Compacted Opaque Forward Plus Mesh Pipeline");
                    }),
                    ("mesh.forward.simple", () =>
                {
                    _forwardSimplePipeline = CreateGraphicsPipeline(
                        forwardTaskShaderName, "forward_simple.mesh.spv",
                        forwardOpaqueSimpleFragmentShaderName, colorFormat,
                        depthFormat, true, false, false, CullModeFlags.None,
                        false, materialTransportProvenanceFormat:
                        materialTransportProvenanceFormat);
                    _context.SetDebugName(_forwardSimplePipeline.Handle,
                        ObjectType.Pipeline,
                        "Simple Opaque Forward Plus Mesh Pipeline");
                }),
                ("mesh.forward.simple-full-input", () =>
                {
                    _forwardSimpleFullInputPipeline = CreateGraphicsPipeline(
                        forwardTaskShaderName, "forward.mesh.spv",
                        forwardOpaqueSimpleFullInputFragmentShaderName,
                        colorFormat, depthFormat, true, false, false,
                        CullModeFlags.None, false,
                        materialTransportProvenanceFormat:
                        materialTransportProvenanceFormat);
                    _context.SetDebugName(
                        _forwardSimpleFullInputPipeline.Handle,
                        ObjectType.Pipeline,
                        "Simple Full-Input Opaque Forward Plus Mesh Pipeline");
                }),
                ("mesh.forward.compacted-simple", () =>
                {
                    _forwardCompactedSimplePipeline = CreateGraphicsPipeline(
                        null, _compactedForwardSimpleMeshShaderName,
                        forwardOpaqueSimpleFragmentShaderName, colorFormat,
                        depthFormat, true, false, false, CullModeFlags.None,
                        false, materialTransportProvenanceFormat:
                        materialTransportProvenanceFormat);
                    _context.SetDebugName(
                        _forwardCompactedSimplePipeline.Handle,
                        ObjectType.Pipeline,
                        "Compacted Simple Opaque Forward Plus Mesh Pipeline");
                }),
                ("mesh.forward.compacted-simple-full-input", () =>
                {
                    _forwardCompactedSimpleFullInputPipeline =
                        CreateGraphicsPipeline(
                            null, _compactedForwardMeshShaderName,
                            forwardOpaqueSimpleFullInputFragmentShaderName,
                            colorFormat, depthFormat, true, false, false,
                            CullModeFlags.None, false,
                            materialTransportProvenanceFormat:
                            materialTransportProvenanceFormat);
                    _context.SetDebugName(
                        _forwardCompactedSimpleFullInputPipeline.Handle,
                        ObjectType.Pipeline,
                        "Compacted Simple Full-Input Opaque Forward Plus Mesh Pipeline");
                    }));
            }

            // C4 and C5 can independently have zero work on any frame. Admit
            // each semantic MRT set even when both features are configured so
            // a missing C4 hero source cannot make C5 select a nonexistent
            // standalone pipeline (and vice versa). The combined four-target
            // set is an additional fast path, not a replacement for either
            // independently valid producer contract.
            CreateNearFieldDirectSourcePipelines(
                colorFormat,
                depthFormat,
                forwardTaskShaderName,
                materialTransportProvenanceEnabled);
            CreateGiCausticReceiverPipelines(
                colorFormat,
                depthFormat,
                forwardTaskShaderName,
                materialTransportProvenanceEnabled);
            if (NearFieldDirectSourceAttachmentEnabled &&
                GiCausticReceiverAttachmentEnabled &&
                _nearFieldDirectSourceConfiguration.SourceProducerMode ==
                    SimpleDdgiNearFieldSourceProducerMode.ForwardMrt)
            {
                CreateCombinedAdvancedGiPipelines(
                    colorFormat,
                    depthFormat,
                    forwardTaskShaderName,
                    materialTransportProvenanceEnabled);
            }
            CreateHybridReflectionPipelines(materialTransportProvenanceEnabled);

#if !DEBUG && !NJULF_DETAILED_INVESTIGATION
            if (!materialTransportProvenanceEnabled &&
                !RendererBuildConfiguration.FastPipelineStartup)
            {
                const string cacheFullFragmentShaderName =
                    "forward_opaque_ddgi_cache_required.frag.spv";
                const string cacheSimpleFragmentShaderName =
                    "forward_opaque_simple_ddgi_cache_required.frag.spv";
                const string cacheSimpleFullInputFragmentShaderName =
                    "forward_opaque_simple_full_input_ddgi_cache_required.frag.spv";

                CreateOpaqueSpecializedPipelineSet(
                    colorFormat,
                    depthFormat,
                    forwardTaskShaderName,
                    cacheFullFragmentShaderName,
                    cacheSimpleFragmentShaderName,
                    cacheSimpleFullInputFragmentShaderName,
                    "Receiver-Cache",
                    out _forwardReceiverCachePipeline,
                    out _forwardCompactedReceiverCachePipeline,
                    out _forwardSimpleReceiverCachePipeline,
                    out _forwardSimpleFullInputReceiverCachePipeline,
                    out _forwardCompactedSimpleReceiverCachePipeline,
                    out _forwardCompactedSimpleFullInputReceiverCachePipeline);

                if (SimpleDdgiReceiverCachePolicy.ResolveRequestedMode(
                        Settings.GlobalIllumination
                            .SimpleDdgiReceiverCacheMode,
                        Settings.Diagnostics
                            .ForceForwardGiReceiverCacheForBenchmark,
                        Settings.Diagnostics
                            .ForceExactForwardGiGatherForBenchmark) ==
                    SimpleDdgiReceiverCacheMode.LegacyDepthOnlyBenchmark)
                {
                    CreateOpaqueSpecializedPipelineSet(
                        colorFormat,
                        depthFormat,
                        forwardTaskShaderName,
                        "forward_opaque_ddgi_cache_legacy.frag.spv",
                        "forward_opaque_simple_ddgi_cache_legacy.frag.spv",
                        "forward_opaque_simple_full_input_ddgi_cache_legacy.frag.spv",
                        "Legacy Depth-Only Receiver-Cache Benchmark",
                        out _forwardReceiverCacheLegacyPipeline,
                        out _forwardCompactedReceiverCacheLegacyPipeline,
                        out _forwardSimpleReceiverCacheLegacyPipeline,
                        out _forwardSimpleFullInputReceiverCacheLegacyPipeline,
                        out _forwardCompactedSimpleReceiverCacheLegacyPipeline,
                        out _forwardCompactedSimpleFullInputReceiverCacheLegacyPipeline);
                }

                if (Settings.Diagnostics.DdgiForwardEstimateCountersEnabled)
                {
                    CreateOpaqueSpecializedPipelineSet(
                        colorFormat,
                        depthFormat,
                        forwardTaskShaderName,
                        "forward_opaque_ddgi_cache_required_diagnostics.frag.spv",
                        "forward_opaque_simple_ddgi_cache_required_diagnostics.frag.spv",
                        "forward_opaque_simple_full_input_ddgi_cache_required_diagnostics.frag.spv",
                        "Surface-Aware Receiver-Cache Diagnostics",
                        out _forwardReceiverCacheDiagnosticsPipeline,
                        out _forwardCompactedReceiverCacheDiagnosticsPipeline,
                        out _forwardSimpleReceiverCacheDiagnosticsPipeline,
                        out _forwardSimpleFullInputReceiverCacheDiagnosticsPipeline,
                        out _forwardCompactedSimpleReceiverCacheDiagnosticsPipeline,
                        out _forwardCompactedSimpleFullInputReceiverCacheDiagnosticsPipeline);
                }

                CreateOpaqueSpecializedPipelineSet(
                    colorFormat,
                    depthFormat,
                    forwardTaskShaderName,
                    "forward_opaque_ddgi_cache_debug.frag.spv",
                    "forward_opaque_simple_ddgi_cache_debug.frag.spv",
                    "forward_opaque_simple_full_input_ddgi_cache_debug.frag.spv",
                    "Surface-Aware Receiver-Cache Debug",
                    out _forwardReceiverCacheDebugPipeline,
                    out _forwardCompactedReceiverCacheDebugPipeline,
                    out _forwardSimpleReceiverCacheDebugPipeline,
                    out _forwardSimpleFullInputReceiverCacheDebugPipeline,
                    out _forwardCompactedSimpleReceiverCacheDebugPipeline,
                    out _forwardCompactedSimpleFullInputReceiverCacheDebugPipeline);

                CreateOpaqueSpecializedPipelineSet(
                    colorFormat,
                    depthFormat,
                    forwardTaskShaderName,
                    "forward_opaque_gi_disabled.frag.spv",
                    "forward_opaque_simple_gi_disabled.frag.spv",
                    "forward_opaque_simple_full_input_gi_disabled.frag.spv",
                    "GI-Disabled Control",
                    out _forwardGiDisabledPipeline,
                    out _forwardCompactedGiDisabledPipeline,
                    out _forwardSimpleGiDisabledPipeline,
                    out _forwardSimpleFullInputGiDisabledPipeline,
                    out _forwardCompactedSimpleGiDisabledPipeline,
                    out _forwardCompactedSimpleFullInputGiDisabledPipeline);
            }
#endif

            string? transparentTaskShaderName =
                TasklessSubmissionEnabled
                    ? null
                    : forwardTaskShaderName;
            string transparentMeshShaderName =
                TasklessSubmissionEnabled
                    ? _compactedForwardMeshShaderName
                    : "forward.mesh.spv";
            _transparentTaskShaderName = transparentTaskShaderName;
            _transparentMeshShaderName = transparentMeshShaderName;
            if (!RendererBuildConfiguration.FastPipelineStartup)
            {
                EnsureTransparentForwardPipeline();
                EnsureThinGlassForwardPipeline();
                EnsureGeometryDecalOverlayPipeline();
            }

            if (!RendererBuildConfiguration.FastPipelineStartup)
                EnsureWeightedOitTransparentPipeline();

            AdmitRayTransparentPipelines();

            CreateReceiverFeedbackPipelines(
                colorFormat,
                depthFormat,
                forwardTaskShaderName,
                transparentTaskShaderName,
                transparentMeshShaderName,
                materialTransportProvenanceFormat);

            RunPipelineCreationBatch(
                ("mesh.motion", () =>
                {
                    _motionVectorPipeline = CreateGraphicsPipeline(
                        "motion_vector.task.spv", "motion_vector.mesh.spv",
                        "motion_vector.frag.spv",
                        RenderTargetManager.MotionVectorFormat, depthFormat,
                        true, false, false, CullModeFlags.None, false);
                    _context.SetDebugName(_motionVectorPipeline.Handle,
                        ObjectType.Pipeline,
                        "Solid Motion Vector Mesh Pipeline");
                }),
                ("mesh.motion.masked", () =>
                {
                    _maskedMotionVectorPipeline = CreateGraphicsPipeline(
                        "motion_vector.task.spv",
                        "motion_vector_alpha.mesh.spv",
                        "motion_vector_alpha.frag.spv",
                        RenderTargetManager.MotionVectorFormat, depthFormat,
                        true, false, false, CullModeFlags.None, false);
                    _context.SetDebugName(_maskedMotionVectorPipeline.Handle,
                        ObjectType.Pipeline,
                        "Masked Motion Vector Mesh Pipeline");
                }),
                ("mesh.motion.compacted", () =>
                {
                    _compactedMotionVectorPipeline = CreateGraphicsPipeline(
                        null, _compactedMotionVectorMeshShaderName,
                        "motion_vector.frag.spv",
                        RenderTargetManager.MotionVectorFormat, depthFormat,
                        true, false, false, CullModeFlags.None, false);
                    _context.SetDebugName(
                        _compactedMotionVectorPipeline.Handle,
                        ObjectType.Pipeline,
                        "Compacted Mesh-Only Solid Motion Vector Pipeline");
                }),
                ("mesh.motion.compacted-masked", () =>
                {
                    _compactedMaskedMotionVectorPipeline =
                        CreateGraphicsPipeline(
                            null, _compactedMotionVectorAlphaMeshShaderName,
                            "motion_vector_alpha.frag.spv",
                            RenderTargetManager.MotionVectorFormat, depthFormat,
                            true, false, false, CullModeFlags.None, false);
                    _context.SetDebugName(
                        _compactedMaskedMotionVectorPipeline.Handle,
                        ObjectType.Pipeline,
                        "Compacted Mesh-Only Masked Motion Vector Pipeline");
                }));

        }

        private void EnsureTransparentForwardPipeline()
        {
            if (_transparentForwardPipeline.Handle != 0)
                return;

            _transparentForwardPipeline = CreateGraphicsPipeline(
                _transparentTaskShaderName,
                _transparentMeshShaderName,
                "forward.frag.spv",
                _colorFormat,
                _depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: false,
                blendEnable: true,
                cullMode: CullModeFlags.None,
                depthBiasEnable: false);
            _context.SetDebugName(
                _transparentForwardPipeline.Handle,
                ObjectType.Pipeline,
                "Transparent Forward Plus Mesh Pipeline");
        }

        private void EnsureThinGlassForwardPipeline()
        {
            if (_thinGlassForwardPipeline.Handle != 0)
                return;

            _thinGlassForwardPipeline = CreateGraphicsPipeline(
                _transparentTaskShaderName,
                _transparentMeshShaderName,
                "forward_transparent_thin_glass.frag.spv",
                _colorFormat,
                _depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: false,
                blendEnable: true,
                cullMode: CullModeFlags.None,
                depthBiasEnable: false);
            _context.SetDebugName(
                _thinGlassForwardPipeline.Handle,
                ObjectType.Pipeline,
                "DDGI Directional Thin Glass Mesh Pipeline");
        }

        private void EnsureGeometryDecalOverlayPipeline()
        {
            if (_geometryDecalOverlayPipeline.Handle != 0)
                return;

            _geometryDecalOverlayPipeline = CreateGraphicsPipeline(
                _transparentTaskShaderName,
                _transparentMeshShaderName,
                "geometry_decal.frag.spv",
                _colorFormat,
                _depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: false,
                blendEnable: true,
                cullMode: CullModeFlags.None,
                depthBiasEnable: false,
                destinationColorModulationBlend: true);
            _context.SetDebugName(
                _geometryDecalOverlayPipeline.Handle,
                ObjectType.Pipeline,
                "Geometry Decal Destination Modulation Mesh Pipeline");
        }

        private void AdmitRayTransparentPipelines()
        {
            _rayTransparentPipelineState = DeferredPipelineState.NotAdmitted;
            _rayWeightedOitPipelineState = DeferredPipelineState.NotAdmitted;
            if (_rayTransparentLayout.Handle == 0)
                return;

            _rayTransparentPipelineState = DeferredPipelineState.Deferred;
            _rayWeightedOitPipelineState = DeferredPipelineState.Deferred;
            RayTransparentPipelineFailureReason =
                "ray-query transparent pipelines deferred until first use";
            if (RendererBuildConfiguration.FastPipelineStartup)
                return;

            if (TryEnsureRayTransparentPipelines() &&
                !TryEnsureRayWeightedOitTransparentPipeline())
            {
                DestroyOptionalPipeline(ref _rayTransparentForwardPipeline);
                _rayTransparentPipelineState = DeferredPipelineState.Failed;
                RayTransparentPipelineFailureReason =
                    "ray-query-weighted-oit-pipeline-creation-failed";
            }
        }

        internal bool TryEnsureRayTransparentPipelines()
        {
            if (_rayTransparentPipelineState == DeferredPipelineState.Ready)
                return _rayTransparentForwardPipeline.Handle != 0;
            if (_rayTransparentPipelineState != DeferredPipelineState.Deferred ||
                _rayTransparentLayout.Handle == 0)
            {
                return false;
            }

            try
            {
                _rayTransparentForwardPipeline = CreateGraphicsPipeline(
                    _transparentTaskShaderName,
                    _transparentMeshShaderName,
                    "forward_transparent_ray.frag.spv",
                    _colorFormat,
                    _depthFormat,
                    hasColorAttachment: true,
                    depthWriteEnable: false,
                    blendEnable: true,
                    cullMode: CullModeFlags.None,
                    depthBiasEnable: false,
                    pipelineLayout: _rayTransparentLayout);
                _context.SetDebugName(
                    _rayTransparentForwardPipeline.Handle,
                    ObjectType.Pipeline,
                    "Ray Query Transparent Forward Plus Mesh Pipeline");
                _rayTransparentPipelineState = DeferredPipelineState.Ready;
                RayTransparentPipelineFailureReason = string.Empty;
                return true;
            }
            catch (Exception exception) when (
                exception is VulkanException or IOException or
                ArgumentException or InvalidOperationException)
            {
                DestroyOptionalPipeline(ref _rayTransparentForwardPipeline);
                DestroyOptionalPipeline(ref _rayWeightedOitTransparentPipeline);
                _rayTransparentPipelineState = DeferredPipelineState.Failed;
                _rayWeightedOitPipelineState = DeferredPipelineState.Failed;
                RayTransparentPipelineFailureReason =
                    "ray-query-transparent-pipeline-creation-failed:" +
                    exception.GetType().Name + ":" + exception.Message;
                System.Diagnostics.Debug.WriteLine(
                    "Ray-query transparent variants are unavailable; " +
                    RayTransparentPipelineFailureReason);
                return false;
            }
        }

        private void CreateReceiverFeedbackPipelines(
            Format colorFormat,
            Format depthFormat,
            string forwardTaskShaderName,
            string? transparentTaskShaderName,
            string transparentMeshShaderName,
            Format? materialTransportProvenanceFormat)
        {
            _alphaMaskReceiverFeedbackPipelineState =
                DeferredPipelineState.NotAdmitted;
            _transparentReceiverFeedbackPipelineState =
                DeferredPipelineState.NotAdmitted;
            _thinGlassReceiverFeedbackPipelineState =
                DeferredPipelineState.NotAdmitted;
            _weightedOitReceiverFeedbackPipelineState =
                DeferredPipelineState.NotAdmitted;
            _rayTransparentReceiverFeedbackPipelineState =
                DeferredPipelineState.NotAdmitted;
            _rayWeightedOitReceiverFeedbackPipelineState =
                DeferredPipelineState.NotAdmitted;
            ReceiverFeedbackPipelineFailureReason =
                "receiver-feedback-pipelines-not-admitted-at-startup";
            if (!_receiverFeedbackPipelinesEnabled)
                return;

            _alphaMaskReceiverFeedbackPipelineState =
                DeferredPipelineState.Deferred;
            _transparentReceiverFeedbackPipelineState =
                DeferredPipelineState.Deferred;
            _thinGlassReceiverFeedbackPipelineState =
                DeferredPipelineState.Deferred;
            _weightedOitReceiverFeedbackPipelineState =
                DeferredPipelineState.Deferred;
            if (RayTransparentPipelinesAdmitted)
            {
                _rayTransparentReceiverFeedbackPipelineState =
                    DeferredPipelineState.Deferred;
                _rayWeightedOitReceiverFeedbackPipelineState =
                    DeferredPipelineState.Deferred;
            }
            if (RendererBuildConfiguration.FastPipelineStartup)
            {
                ReceiverFeedbackPipelineFailureReason =
                    "receiver-feedback-pipelines-deferred-until-first-use";
                return;
            }

            try
            {
                string provenanceSuffix =
                    materialTransportProvenanceFormat.HasValue
                        ? "_provenance"
                        : string.Empty;
                CreateOpaqueSpecializedPipelineSet(
                    colorFormat,
                    depthFormat,
                    forwardTaskShaderName,
                    $"forward_opaque_ddgi_b1{provenanceSuffix}.frag.spv",
                    $"forward_opaque_simple_ddgi_b1{provenanceSuffix}.frag.spv",
                    $"forward_opaque_simple_full_input_ddgi_b1{provenanceSuffix}.frag.spv",
                    "B1 Exact Alpha-Mask Receiver Feedback",
                    out _forwardAlphaMaskReceiverFeedbackPipeline,
                    out _forwardCompactedAlphaMaskReceiverFeedbackPipeline,
                    out _forwardSimpleAlphaMaskReceiverFeedbackPipeline,
                    out _forwardSimpleFullInputAlphaMaskReceiverFeedbackPipeline,
                    out _forwardCompactedSimpleAlphaMaskReceiverFeedbackPipeline,
                    out _forwardCompactedSimpleFullInputAlphaMaskReceiverFeedbackPipeline,
                    materialTransportProvenanceFormat:
                        materialTransportProvenanceFormat);
                _alphaMaskReceiverFeedbackPipelineState =
                    DeferredPipelineState.Ready;

                _transparentReceiverFeedbackPipeline = CreateGraphicsPipeline(
                    transparentTaskShaderName,
                    transparentMeshShaderName,
                    "forward_transparent_ddgi_b1.frag.spv",
                    colorFormat,
                    depthFormat,
                    hasColorAttachment: true,
                    depthWriteEnable: false,
                    blendEnable: true,
                    cullMode: CullModeFlags.None,
                    depthBiasEnable: false);
                _context.SetDebugName(
                    _transparentReceiverFeedbackPipeline.Handle,
                    ObjectType.Pipeline,
                    "B1 Exact Transparent Forward Plus Mesh Pipeline");
                _transparentReceiverFeedbackPipelineState =
                    DeferredPipelineState.Ready;

                _thinGlassReceiverFeedbackPipeline = CreateGraphicsPipeline(
                    transparentTaskShaderName,
                    transparentMeshShaderName,
                    "forward_transparent_thin_glass_ddgi_b1.frag.spv",
                    colorFormat,
                    depthFormat,
                    hasColorAttachment: true,
                    depthWriteEnable: false,
                    blendEnable: true,
                    cullMode: CullModeFlags.None,
                    depthBiasEnable: false);
                _context.SetDebugName(
                    _thinGlassReceiverFeedbackPipeline.Handle,
                    ObjectType.Pipeline,
                    "B1 Exact DDGI Directional Thin Glass Mesh Pipeline");
                _thinGlassReceiverFeedbackPipelineState =
                    DeferredPipelineState.Ready;

                if (!TryEnsureWeightedOitReceiverFeedbackPipeline())
                {
                    throw new InvalidOperationException(
                        ReceiverFeedbackPipelineFailureReason);
                }

                TryEnsureRayTransparentReceiverFeedbackPipeline();

                ReceiverFeedbackPipelineFailureReason =
                    "receiver-feedback-pipelines-ready";
            }
            catch (Exception exception) when (
                exception is VulkanException or IOException or
                ArgumentException or InvalidOperationException)
            {
                // B1 is optional. A shader-module or native driver compiler
                // failure must never take canonical DDGI or forward rendering
                // down with it, and a partial set must never be selectable.
                DestroyAlphaMaskReceiverFeedbackPipelines();
                DestroyTransparentReceiverFeedbackPipelines();
                MarkReceiverFeedbackPipelineStatesFailed();
                ReceiverFeedbackPipelineFailureReason =
                    "receiver-feedback-pipeline-creation-failed:" +
                    exception.GetType().Name + ":" + exception.Message;
                System.Diagnostics.Debug.WriteLine(
                    "B1 receiver-feedback pipelines unavailable; canonical " +
                    "forward rendering retained. " +
                    ReceiverFeedbackPipelineFailureReason);
            }
        }

        internal bool TryEnsureAlphaMaskReceiverFeedbackPipelines()
        {
            if (_alphaMaskReceiverFeedbackPipelineState ==
                DeferredPipelineState.Ready)
            {
                return AlphaMaskReceiverFeedbackPipelinesAvailable;
            }
            if (_alphaMaskReceiverFeedbackPipelineState !=
                    DeferredPipelineState.Deferred ||
                !_receiverFeedbackPipelinesEnabled)
            {
                return false;
            }

            try
            {
                string provenanceSuffix =
                    _materialTransportProvenanceFormat.HasValue
                        ? "_provenance"
                        : string.Empty;
                CreateOpaqueSpecializedPipelineSet(
                    _colorFormat,
                    _depthFormat,
                    _forwardTaskShaderName,
                    $"forward_opaque_ddgi_b1{provenanceSuffix}.frag.spv",
                    $"forward_opaque_simple_ddgi_b1{provenanceSuffix}.frag.spv",
                    $"forward_opaque_simple_full_input_ddgi_b1{provenanceSuffix}.frag.spv",
                    "B1 Exact Alpha-Mask Receiver Feedback",
                    out _forwardAlphaMaskReceiverFeedbackPipeline,
                    out _forwardCompactedAlphaMaskReceiverFeedbackPipeline,
                    out _forwardSimpleAlphaMaskReceiverFeedbackPipeline,
                    out _forwardSimpleFullInputAlphaMaskReceiverFeedbackPipeline,
                    out _forwardCompactedSimpleAlphaMaskReceiverFeedbackPipeline,
                    out _forwardCompactedSimpleFullInputAlphaMaskReceiverFeedbackPipeline,
                    materialTransportProvenanceFormat:
                        _materialTransportProvenanceFormat);
                _alphaMaskReceiverFeedbackPipelineState =
                    DeferredPipelineState.Ready;
                ReceiverFeedbackPipelineFailureReason =
                    "alpha-mask-receiver-feedback-pipelines-ready";
                return true;
            }
            catch (Exception exception) when (
                exception is VulkanException or IOException or
                ArgumentException or InvalidOperationException)
            {
                DestroyAlphaMaskReceiverFeedbackPipelines();
                _alphaMaskReceiverFeedbackPipelineState =
                    DeferredPipelineState.Failed;
                ReceiverFeedbackPipelineFailureReason =
                    "alpha-mask-receiver-feedback-pipeline-creation-failed:" +
                    exception.GetType().Name + ":" + exception.Message;
                System.Diagnostics.Debug.WriteLine(
                    "B1 alpha-mask receiver-feedback pipelines unavailable; " +
                    ReceiverFeedbackPipelineFailureReason);
                return false;
            }
        }

        internal bool TryEnsureTransparentReceiverFeedbackPipeline(
            bool thinGlass)
        {
            return thinGlass
                ? TryEnsureTransparentReceiverFeedbackPipeline(
                    ref _thinGlassReceiverFeedbackPipeline,
                    ref _thinGlassReceiverFeedbackPipelineState,
                    "forward_transparent_thin_glass_ddgi_b1.frag.spv",
                    "B1 Exact DDGI Directional Thin Glass Mesh Pipeline",
                    "thin-glass")
                : TryEnsureTransparentReceiverFeedbackPipeline(
                    ref _transparentReceiverFeedbackPipeline,
                    ref _transparentReceiverFeedbackPipelineState,
                    "forward_transparent_ddgi_b1.frag.spv",
                    "B1 Exact Transparent Forward Plus Mesh Pipeline",
                    "transparent");
        }

        private bool TryEnsureTransparentReceiverFeedbackPipeline(
            ref VkPipeline pipeline,
            ref DeferredPipelineState state,
            string fragmentShader,
            string debugName,
            string failureKind)
        {
            if (state == DeferredPipelineState.Ready)
                return pipeline.Handle != 0;
            if (state != DeferredPipelineState.Deferred ||
                !_receiverFeedbackPipelinesEnabled)
            {
                return false;
            }

            try
            {
                pipeline = CreateGraphicsPipeline(
                    _transparentTaskShaderName,
                    _transparentMeshShaderName,
                    fragmentShader,
                    _colorFormat,
                    _depthFormat,
                    hasColorAttachment: true,
                    depthWriteEnable: false,
                    blendEnable: true,
                    cullMode: CullModeFlags.None,
                    depthBiasEnable: false);
                _context.SetDebugName(
                    pipeline.Handle,
                    ObjectType.Pipeline,
                    debugName);
                state = DeferredPipelineState.Ready;
                ReceiverFeedbackPipelineFailureReason =
                    failureKind + "-receiver-feedback-pipeline-ready";
                return true;
            }
            catch (Exception exception) when (
                exception is VulkanException or IOException or
                ArgumentException or InvalidOperationException)
            {
                DestroyOptionalPipeline(ref pipeline);
                state = DeferredPipelineState.Failed;
                ReceiverFeedbackPipelineFailureReason =
                    failureKind +
                    "-receiver-feedback-pipeline-creation-failed:" +
                    exception.GetType().Name + ":" + exception.Message;
                System.Diagnostics.Debug.WriteLine(
                    "B1 transparent receiver-feedback pipeline unavailable; " +
                    ReceiverFeedbackPipelineFailureReason);
                return false;
            }
        }

        internal bool TryEnsureRayTransparentReceiverFeedbackPipeline()
        {
            if (_rayTransparentReceiverFeedbackPipelineState ==
                DeferredPipelineState.Ready)
            {
                return _rayTransparentReceiverFeedbackPipeline.Handle != 0;
            }
            if (_rayTransparentReceiverFeedbackPipelineState !=
                    DeferredPipelineState.Deferred ||
                !TryEnsureRayTransparentPipelines())
            {
                return false;
            }

            try
            {
                _rayTransparentReceiverFeedbackPipeline = CreateGraphicsPipeline(
                    _transparentTaskShaderName,
                    _transparentMeshShaderName,
                    "forward_transparent_ray_ddgi_b1.frag.spv",
                    _colorFormat,
                    _depthFormat,
                    hasColorAttachment: true,
                    depthWriteEnable: false,
                    blendEnable: true,
                    cullMode: CullModeFlags.None,
                    depthBiasEnable: false,
                    pipelineLayout: _rayTransparentLayout);
                _rayTransparentReceiverFeedbackPipelineState =
                    DeferredPipelineState.Ready;
                if (!RendererBuildConfiguration.FastPipelineStartup &&
                    !TryEnsureRayWeightedOitReceiverFeedbackPipeline())
                {
                    DestroyOptionalPipeline(
                        ref _rayTransparentReceiverFeedbackPipeline);
                    _rayTransparentReceiverFeedbackPipelineState =
                        DeferredPipelineState.Failed;
                    return false;
                }
                return true;
            }
            catch (Exception exception) when (
                exception is VulkanException or IOException or
                ArgumentException or InvalidOperationException)
            {
                DestroyOptionalPipeline(
                    ref _rayTransparentReceiverFeedbackPipeline);
                DestroyOptionalPipeline(
                    ref _rayWeightedOitReceiverFeedbackPipeline);
                _rayTransparentReceiverFeedbackPipelineState =
                    DeferredPipelineState.Failed;
                _rayWeightedOitReceiverFeedbackPipelineState =
                    DeferredPipelineState.Failed;
                ReceiverFeedbackPipelineFailureReason =
                    "ray-query-receiver-feedback-pipeline-creation-failed:" +
                    exception.GetType().Name + ":" + exception.Message;
                System.Diagnostics.Debug.WriteLine(
                    "Combined ray-query/B1 transparent variants are unavailable: " +
                    exception.Message);
                return false;
            }
        }

        private void EnsureWeightedOitTransparentPipeline()
        {
            if (_weightedOitTransparentPipeline.Handle != 0)
                return;

            _weightedOitTransparentPipeline =
                CreateWeightedOitGraphicsPipeline(
                    _transparentTaskShaderName,
                    _transparentMeshShaderName,
                    "forward_weighted_oit.frag.spv",
                    RenderTargetManager.WeightedOitAccumulationFormat,
                    RenderTargetManager.WeightedOitRevealageFormat,
                    _depthFormat);
            _context.SetDebugName(
                _weightedOitTransparentPipeline.Handle,
                ObjectType.Pipeline,
                "Weighted OIT Transparent Mesh Pipeline");
        }

        internal bool TryEnsureRayWeightedOitTransparentPipeline()
        {
            if (_rayWeightedOitPipelineState == DeferredPipelineState.Ready)
                return _rayWeightedOitTransparentPipeline.Handle != 0;
            if (_rayWeightedOitPipelineState != DeferredPipelineState.Deferred ||
                !TryEnsureRayTransparentPipelines())
            {
                return false;
            }

            try
            {
                _rayWeightedOitTransparentPipeline =
                    CreateWeightedOitGraphicsPipeline(
                    _transparentTaskShaderName,
                    _transparentMeshShaderName,
                    "forward_weighted_oit_ray.frag.spv",
                    RenderTargetManager.WeightedOitAccumulationFormat,
                    RenderTargetManager.WeightedOitRevealageFormat,
                        _depthFormat,
                        _rayTransparentLayout);
                _context.SetDebugName(
                    _rayWeightedOitTransparentPipeline.Handle,
                    ObjectType.Pipeline,
                    "Ray Query Weighted OIT Transparent Mesh Pipeline");
                _rayWeightedOitPipelineState = DeferredPipelineState.Ready;
                return true;
            }
            catch (Exception exception) when (
                exception is VulkanException or IOException or
                ArgumentException or InvalidOperationException)
            {
                DestroyOptionalPipeline(ref _rayWeightedOitTransparentPipeline);
                _rayWeightedOitPipelineState = DeferredPipelineState.Failed;
                System.Diagnostics.Debug.WriteLine(
                    "Ray-query weighted OIT pipeline unavailable: " +
                    exception.Message);
                return false;
            }
        }

        internal bool TryEnsureWeightedOitReceiverFeedbackPipeline()
        {
            if (_weightedOitReceiverFeedbackPipelineState ==
                DeferredPipelineState.Ready)
            {
                return _weightedOitReceiverFeedbackPipeline.Handle != 0;
            }
            if (_weightedOitReceiverFeedbackPipelineState !=
                    DeferredPipelineState.Deferred ||
                !_receiverFeedbackPipelinesEnabled)
            {
                return false;
            }

            try
            {
                _weightedOitReceiverFeedbackPipeline =
                    CreateWeightedOitGraphicsPipeline(
                    _transparentTaskShaderName,
                    _transparentMeshShaderName,
                    "forward_weighted_oit_ddgi_b1.frag.spv",
                    RenderTargetManager.WeightedOitAccumulationFormat,
                        RenderTargetManager.WeightedOitRevealageFormat,
                        _depthFormat);
                _context.SetDebugName(
                    _weightedOitReceiverFeedbackPipeline.Handle,
                    ObjectType.Pipeline,
                    "B1 Exact Weighted OIT Transparent Mesh Pipeline");
                _weightedOitReceiverFeedbackPipelineState =
                    DeferredPipelineState.Ready;
                return true;
            }
            catch (Exception exception) when (
                exception is VulkanException or IOException or
                ArgumentException or InvalidOperationException)
            {
                DestroyOptionalPipeline(
                    ref _weightedOitReceiverFeedbackPipeline);
                _weightedOitReceiverFeedbackPipelineState =
                    DeferredPipelineState.Failed;
                ReceiverFeedbackPipelineFailureReason =
                    "weighted-oit-receiver-feedback-pipeline-creation-failed:" +
                    exception.GetType().Name + ":" + exception.Message;
                return false;
            }
        }

        internal bool TryEnsureRayWeightedOitReceiverFeedbackPipeline()
        {
            if (_rayWeightedOitReceiverFeedbackPipelineState ==
                DeferredPipelineState.Ready)
            {
                return _rayWeightedOitReceiverFeedbackPipeline.Handle != 0;
            }
            if (_rayWeightedOitReceiverFeedbackPipelineState !=
                    DeferredPipelineState.Deferred ||
                !_receiverFeedbackPipelinesEnabled ||
                !TryEnsureRayWeightedOitTransparentPipeline())
            {
                return false;
            }

            try
            {
                _rayWeightedOitReceiverFeedbackPipeline =
                    CreateWeightedOitGraphicsPipeline(
                    _transparentTaskShaderName,
                    _transparentMeshShaderName,
                    "forward_weighted_oit_ray_ddgi_b1.frag.spv",
                    RenderTargetManager.WeightedOitAccumulationFormat,
                    RenderTargetManager.WeightedOitRevealageFormat,
                        _depthFormat,
                        _rayTransparentLayout);
                _context.SetDebugName(
                    _rayWeightedOitReceiverFeedbackPipeline.Handle,
                    ObjectType.Pipeline,
                    "Ray Query B1 Exact Weighted OIT Transparent Mesh Pipeline");
                _rayWeightedOitReceiverFeedbackPipelineState =
                    DeferredPipelineState.Ready;
                return true;
            }
            catch (Exception exception) when (
                exception is VulkanException or IOException or
                ArgumentException or InvalidOperationException)
            {
                DestroyOptionalPipeline(
                    ref _rayWeightedOitReceiverFeedbackPipeline);
                _rayWeightedOitReceiverFeedbackPipelineState =
                    DeferredPipelineState.Failed;
                ReceiverFeedbackPipelineFailureReason =
                    "ray-weighted-oit-receiver-feedback-pipeline-creation-failed:" +
                    exception.GetType().Name + ":" + exception.Message;
                return false;
            }
        }

        private void CreateHybridReflectionPipelines(
            bool materialTransportProvenanceEnabled)
        {
            if (!_hybridReflectionConfiguration.Enabled ||
                _hybridReflectionConfiguration.ShaderSemanticVersion !=
                    ForwardHybridReflectionReceiverContract.ShaderSemanticVersion)
            {
                HybridReflectionFailureReason =
                    "hybrid-reflection-receiver-configuration-invalid";
                return;
            }
            if (materialTransportProvenanceEnabled)
            {
                HybridReflectionFailureReason =
                    "hybrid-reflection-receiver-material-provenance-conflict";
                return;
            }

            // The 24 possible opaque combinations are materialized lazily on
            // first use. This keeps static-probe and SSR-only startup bounded.
            HybridReflectionAttachmentEnabled = true;
            HybridReflectionFailureReason = "valid; variants created on first use";
        }

        private bool TryCreateHybridReflectionPipeline(
            int combination,
            int family,
            int receiverLane)
        {
            bool receiverCacheRequired = receiverLane !=
                HybridReflectionExactLane;
            uint receiverCacheLane = receiverLane switch
            {
                HybridReflectionCacheAcceptedPipelineLane =>
                    ForwardReceiverCacheAcceptedLane,
                HybridReflectionCacheFallbackPipelineLane =>
                    ForwardReceiverCacheExactFallbackLane,
                _ => ForwardReceiverCacheCombinedLane
            };
            bool giCaustic = (combination & 1) != 0;
            bool nearField = (combination & 2) != 0;
            bool simple = family is 2 or 3 or 4 or 5;
            bool simpleFullInput = family is 3 or 5;
            bool compacted = family is 1 or 4 or 5;
            string fragmentShader =
                ForwardHybridReflectionReceiverContract.ResolveFragmentShader(
                    simple,
                    simpleFullInput,
                    giCaustic,
                    nearField,
                    receiverCacheRequired);
            string meshShader = simple && !simpleFullInput
                ? compacted
                    ? _compactedForwardSimpleMeshShaderName
                    : "forward_simple.mesh.spv"
                : compacted
                    ? _compactedForwardMeshShaderName
                    : "forward.mesh.spv";
            string? taskShader = compacted ? null : _forwardTaskShaderName;

            Format? secondary = combination switch
            {
                0 => ForwardHybridReflectionReceiverContract.ReceiverPayloadFormat,
                1 => ForwardGiCausticReceiverContract.ReceiverPayloadFormat,
                2 => ForwardNearFieldDirectSourceContract.RequiredAttachmentFormat,
                3 => ForwardGiCausticReceiverContract.ReceiverPayloadFormat,
                _ => null
            };
            Format? tertiary = combination switch
            {
                0 => ForwardHybridReflectionReceiverContract.LobeExtensionFormat,
                1 => ForwardHybridReflectionReceiverContract.ReceiverPayloadFormat,
                2 => ForwardNearFieldDirectSourceContract.ReceiverPayloadFormat,
                3 => ForwardNearFieldDirectSourceContract.RequiredAttachmentFormat,
                _ => null
            };
            Format? quaternary = combination switch
            {
                1 => ForwardHybridReflectionReceiverContract.LobeExtensionFormat,
                2 => ForwardHybridReflectionReceiverContract.ReceiverPayloadFormat,
                3 => ForwardNearFieldDirectSourceContract.ReceiverPayloadFormat,
                _ => null
            };
            Format? quinary = combination switch
            {
                2 => ForwardHybridReflectionReceiverContract.LobeExtensionFormat,
                3 => ForwardHybridReflectionReceiverContract.ReceiverPayloadFormat,
                _ => null
            };
            Format? senary = combination == 3
                ? ForwardHybridReflectionReceiverContract.LobeExtensionFormat
                : null;

            try
            {
                VkPipeline pipeline = CreateGraphicsPipeline(
                    taskShader,
                    meshShader,
                    fragmentShader,
                    _colorFormat,
                    _depthFormat,
                    hasColorAttachment: true,
                    depthWriteEnable: false,
                    blendEnable: false,
                    cullMode: CullModeFlags.None,
                    depthBiasEnable: false,
                    secondaryColorFormat: secondary,
                    tertiaryColorFormat: tertiary,
                    quaternaryColorFormat: quaternary,
                    quinaryColorFormat: quinary,
                    senaryColorFormat: senary,
                    hybridReflectionReceiverEnabled: true,
                    forwardReceiverCacheLane: receiverCacheLane);
                _context.SetDebugName(
                    pipeline.Handle,
                    ObjectType.Pipeline,
                    $"Hybrid Reflection Forward Pipeline L{receiverLane} C{combination} F{family}");
                _hybridReflectionPipelines[
                    receiverLane,
                    combination,
                    family] = pipeline;
                HybridReflectionFailureReason = "valid";
                return true;
            }
            catch (Exception exception)
            {
                HybridReflectionFailureReason =
                    "hybrid-reflection-pipeline-creation-failed:" +
                    exception.GetType().Name + ":" + exception.Message;
                return false;
            }
        }

        private void CreateNearFieldDirectSourcePipelines(
            Format colorFormat,
            Format depthFormat,
            string forwardTaskShaderName,
            bool materialTransportProvenanceEnabled)
        {
            if (!_nearFieldDirectSourceConfiguration.IsC5EffectivelyEnabled)
                return;

            if (materialTransportProvenanceEnabled)
            {
                NearFieldDirectSourceFailureReason =
                    "near-field-direct-source-material-transport-provenance-conflict";
                return;
            }

            if (!ForwardNearFieldDirectSourceContract
                    .TryValidatePipelineConfiguration(
                        _nearFieldDirectSourceConfiguration,
                        out string failure))
            {
                NearFieldDirectSourceFailureReason = failure;
                return;
            }

            if (RendererBuildConfiguration.FastPipelineStartup)
            {
                NearFieldDirectSourceAttachmentEnabled = true;
                NearFieldDirectSourceFailureReason =
                    "valid; pipeline variants deferred until first use";
                return;
            }

            try
            {
                bool traceResolutionSource =
                    _nearFieldDirectSourceConfiguration.SourceProducerMode ==
                    SimpleDdgiNearFieldSourceProducerMode.TraceResolutionRaster;
                if (traceResolutionSource)
                {
                    CreateOpaqueSpecializedPipelineSet(
                        ForwardNearFieldDirectSourceContract.RequiredAttachmentFormat,
                        depthFormat,
                        forwardTaskShaderName,
                        ForwardNearFieldDirectSourceContract
                            .TraceResolutionOpaqueFragmentShader,
                        ForwardNearFieldDirectSourceContract
                            .TraceResolutionSimpleOpaqueFragmentShader,
                        ForwardNearFieldDirectSourceContract
                            .TraceResolutionSimpleFullInputOpaqueFragmentShader,
                        "Trace-Resolution Near-Field Direct Source",
                        out _forwardNearFieldDirectSourcePipeline,
                        out _forwardCompactedNearFieldDirectSourcePipeline,
                        out _forwardSimpleNearFieldDirectSourcePipeline,
                        out _forwardSimpleFullInputNearFieldDirectSourcePipeline,
                        out _forwardCompactedSimpleNearFieldDirectSourcePipeline,
                        out _forwardCompactedSimpleFullInputNearFieldDirectSourcePipeline,
                        secondaryColorFormat:
                            ForwardNearFieldDirectSourceContract
                                .ReceiverPayloadFormat,
                        depthWriteEnable: true);

                    if (_forwardNearFieldDirectSourcePipeline.Handle == 0 ||
                        _forwardCompactedNearFieldDirectSourcePipeline.Handle == 0 ||
                        _forwardSimpleNearFieldDirectSourcePipeline.Handle == 0 ||
                        _forwardSimpleFullInputNearFieldDirectSourcePipeline.Handle == 0 ||
                        _forwardCompactedSimpleNearFieldDirectSourcePipeline.Handle == 0 ||
                        _forwardCompactedSimpleFullInputNearFieldDirectSourcePipeline.Handle == 0)
                    {
                        DestroyNearFieldDirectSourcePipelines();
                        NearFieldDirectSourceFailureReason =
                            "near-field-trace-source-pipeline-variant-incomplete";
                        return;
                    }

                    NearFieldDirectSourceAttachmentEnabled = true;
                    NearFieldDirectSourceFailureReason = "valid";
                    return;
                }

                CreateOpaqueSpecializedPipelineSet(
                    colorFormat,
                    depthFormat,
                    forwardTaskShaderName,
                    ForwardNearFieldDirectSourceContract.OpaqueFragmentShader,
                    ForwardNearFieldDirectSourceContract.SimpleOpaqueFragmentShader,
                    ForwardNearFieldDirectSourceContract
                        .SimpleFullInputOpaqueFragmentShader,
                    "Near-Field Direct-Diffuse-and-Emissive Source",
                    out _forwardNearFieldDirectSourcePipeline,
                    out _forwardCompactedNearFieldDirectSourcePipeline,
                    out _forwardSimpleNearFieldDirectSourcePipeline,
                    out _forwardSimpleFullInputNearFieldDirectSourcePipeline,
                    out _forwardCompactedSimpleNearFieldDirectSourcePipeline,
                    out _forwardCompactedSimpleFullInputNearFieldDirectSourcePipeline,
                    secondaryColorFormat:
                        ForwardNearFieldDirectSourceContract.RequiredAttachmentFormat,
                    tertiaryColorFormat:
                        ForwardNearFieldDirectSourceContract.ReceiverPayloadFormat);

                CreateOpaqueSpecializedPipelineSet(
                    colorFormat,
                    depthFormat,
                    forwardTaskShaderName,
                    ForwardNearFieldDirectSourceContract
                        .ReceiverCacheOpaqueFragmentShader,
                    ForwardNearFieldDirectSourceContract
                        .ReceiverCacheSimpleOpaqueFragmentShader,
                    ForwardNearFieldDirectSourceContract
                        .ReceiverCacheSimpleFullInputOpaqueFragmentShader,
                    "Near-Field Direct Source with DDGI Receiver Cache",
                    out _forwardReceiverCacheNearFieldDirectSourcePipeline,
                    out _forwardCompactedReceiverCacheNearFieldDirectSourcePipeline,
                    out _forwardSimpleReceiverCacheNearFieldDirectSourcePipeline,
                    out _forwardSimpleFullInputReceiverCacheNearFieldDirectSourcePipeline,
                    out _forwardCompactedSimpleReceiverCacheNearFieldDirectSourcePipeline,
                    out _forwardCompactedSimpleFullInputReceiverCacheNearFieldDirectSourcePipeline,
                    secondaryColorFormat:
                        ForwardNearFieldDirectSourceContract.RequiredAttachmentFormat,
                    tertiaryColorFormat:
                        ForwardNearFieldDirectSourceContract.ReceiverPayloadFormat);

                if (_forwardNearFieldDirectSourcePipeline.Handle == 0 ||
                    _forwardCompactedNearFieldDirectSourcePipeline.Handle == 0 ||
                    _forwardSimpleNearFieldDirectSourcePipeline.Handle == 0 ||
                    _forwardSimpleFullInputNearFieldDirectSourcePipeline.Handle == 0 ||
                    _forwardCompactedSimpleNearFieldDirectSourcePipeline.Handle == 0 ||
                    _forwardCompactedSimpleFullInputNearFieldDirectSourcePipeline.Handle == 0 ||
                    _forwardReceiverCacheNearFieldDirectSourcePipeline.Handle == 0 ||
                    _forwardCompactedReceiverCacheNearFieldDirectSourcePipeline.Handle == 0 ||
                    _forwardSimpleReceiverCacheNearFieldDirectSourcePipeline.Handle == 0 ||
                    _forwardSimpleFullInputReceiverCacheNearFieldDirectSourcePipeline.Handle == 0 ||
                    _forwardCompactedSimpleReceiverCacheNearFieldDirectSourcePipeline.Handle == 0 ||
                    _forwardCompactedSimpleFullInputReceiverCacheNearFieldDirectSourcePipeline.Handle == 0)
                {
                    DestroyNearFieldDirectSourcePipelines();
                    NearFieldDirectSourceFailureReason =
                        "near-field-direct-source-pipeline-variant-incomplete";
                    return;
                }

                NearFieldDirectSourceAttachmentEnabled = true;
                NearFieldDirectSourceFailureReason = "valid";
            }
            catch (Exception ex)
            {
                // C5 is optional. A missing artifact, unsupported MRT format, or
                // native pipeline error must retain the ordinary forward path.
                DestroyNearFieldDirectSourcePipelines();
                NearFieldDirectSourceAttachmentEnabled = false;
                NearFieldDirectSourceFailureReason =
                    "near-field-direct-source-pipeline-creation-failed:" +
                    ex.GetType().Name + ":" + ex.Message;
                System.Diagnostics.Debug.WriteLine(
                    $"C5 direct-source pipeline unavailable: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void CreateGiCausticReceiverPipelines(
            Format colorFormat,
            Format depthFormat,
            string forwardTaskShaderName,
            bool materialTransportProvenanceEnabled)
        {
            if (!_giCausticReceiverConfiguration.IsC4EffectivelyEnabled)
                return;

            if (materialTransportProvenanceEnabled)
            {
                GiCausticReceiverFailureReason =
                    "caustic-forward-receiver-MRT-ownership-conflict";
                return;
            }
            if (!ForwardGiCausticReceiverContract.TryValidatePipelineConfiguration(
                    _giCausticReceiverConfiguration,
                    out string failure))
            {
                GiCausticReceiverFailureReason = failure;
                return;
            }

            if (RendererBuildConfiguration.FastPipelineStartup)
            {
                GiCausticReceiverAttachmentEnabled = true;
                GiCausticReceiverFailureReason =
                    "valid; pipeline variants deferred until first use";
                return;
            }

            try
            {
                CreateOpaqueSpecializedPipelineSet(
                    colorFormat,
                    depthFormat,
                    forwardTaskShaderName,
                    ForwardGiCausticReceiverContract.OpaqueFragmentShader,
                    ForwardGiCausticReceiverContract.SimpleOpaqueFragmentShader,
                    ForwardGiCausticReceiverContract
                        .SimpleFullInputOpaqueFragmentShader,
                    "C4 Current Receiver Payload",
                    out _forwardGiCausticReceiverPipeline,
                    out _forwardCompactedGiCausticReceiverPipeline,
                    out _forwardSimpleGiCausticReceiverPipeline,
                    out _forwardSimpleFullInputGiCausticReceiverPipeline,
                    out _forwardCompactedSimpleGiCausticReceiverPipeline,
                    out _forwardCompactedSimpleFullInputGiCausticReceiverPipeline,
                    secondaryColorFormat:
                        ForwardGiCausticReceiverContract.ReceiverPayloadFormat);

                CreateOpaqueSpecializedPipelineSet(
                    colorFormat,
                    depthFormat,
                    forwardTaskShaderName,
                    ForwardGiCausticReceiverContract
                        .ReceiverCacheOpaqueFragmentShader,
                    ForwardGiCausticReceiverContract
                        .ReceiverCacheSimpleOpaqueFragmentShader,
                    ForwardGiCausticReceiverContract
                        .ReceiverCacheSimpleFullInputOpaqueFragmentShader,
                    "C4 Current Receiver Payload with DDGI Receiver Cache",
                    out _forwardReceiverCacheGiCausticReceiverPipeline,
                    out _forwardCompactedReceiverCacheGiCausticReceiverPipeline,
                    out _forwardSimpleReceiverCacheGiCausticReceiverPipeline,
                    out _forwardSimpleFullInputReceiverCacheGiCausticReceiverPipeline,
                    out _forwardCompactedSimpleReceiverCacheGiCausticReceiverPipeline,
                    out _forwardCompactedSimpleFullInputReceiverCacheGiCausticReceiverPipeline,
                    secondaryColorFormat:
                        ForwardGiCausticReceiverContract.ReceiverPayloadFormat);

                if (_forwardCompactedGiCausticReceiverPipeline.Handle == 0 ||
                    _forwardCompactedSimpleGiCausticReceiverPipeline.Handle == 0 ||
                    _forwardCompactedSimpleFullInputGiCausticReceiverPipeline.Handle == 0 ||
                    _forwardCompactedReceiverCacheGiCausticReceiverPipeline.Handle == 0 ||
                    _forwardCompactedSimpleReceiverCacheGiCausticReceiverPipeline.Handle == 0 ||
                    _forwardCompactedSimpleFullInputReceiverCacheGiCausticReceiverPipeline.Handle == 0 ||
                    (!RendererBuildConfiguration.FastPipelineStartup &&
                     (_forwardGiCausticReceiverPipeline.Handle == 0 ||
                      _forwardSimpleGiCausticReceiverPipeline.Handle == 0 ||
                      _forwardSimpleFullInputGiCausticReceiverPipeline.Handle == 0 ||
                      _forwardReceiverCacheGiCausticReceiverPipeline.Handle == 0 ||
                      _forwardSimpleReceiverCacheGiCausticReceiverPipeline.Handle == 0 ||
                      _forwardSimpleFullInputReceiverCacheGiCausticReceiverPipeline.Handle == 0)))
                {
                    DestroyGiCausticReceiverPipelines();
                    GiCausticReceiverFailureReason =
                        "caustic-forward-receiver-pipeline-variant-incomplete";
                    return;
                }

                GiCausticReceiverAttachmentEnabled = true;
                GiCausticReceiverFailureReason = "valid";
            }
            catch (Exception ex)
            {
                DestroyGiCausticReceiverPipelines();
                GiCausticReceiverAttachmentEnabled = false;
                GiCausticReceiverFailureReason =
                    "caustic-forward-receiver-pipeline-creation-failed:" +
                    ex.GetType().Name + ":" + ex.Message;
                System.Diagnostics.Debug.WriteLine(
                    $"C4 receiver pipeline unavailable: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void CreateCombinedAdvancedGiPipelines(
            Format colorFormat,
            Format depthFormat,
            string forwardTaskShaderName,
            bool materialTransportProvenanceEnabled)
        {
            if (_nearFieldDirectSourceConfiguration.SourceProducerMode !=
                SimpleDdgiNearFieldSourceProducerMode.ForwardMrt)
            {
                CombinedAdvancedGiFailureReason =
                    "combined-advanced-GI-requires-forward-MRT-source";
                return;
            }
            if (materialTransportProvenanceEnabled)
            {
                CombinedAdvancedGiFailureReason =
                    "combined-advanced-GI-material-provenance-conflict";
                return;
            }
            if (!ForwardAdvancedGiCombinedContract
                    .TryValidatePipelineConfigurations(
                        _giCausticReceiverConfiguration,
                        _nearFieldDirectSourceConfiguration,
                        out string failure))
            {
                CombinedAdvancedGiFailureReason = failure;
                return;
            }

            if (RendererBuildConfiguration.FastPipelineStartup)
            {
                CombinedAdvancedGiAttachmentEnabled = true;
                CombinedAdvancedGiFailureReason =
                    "valid; pipeline variants deferred until first use";
                return;
            }

            try
            {
                CreateOpaqueSpecializedPipelineSet(
                    colorFormat,
                    depthFormat,
                    forwardTaskShaderName,
                    ForwardAdvancedGiCombinedContract.OpaqueFragmentShader,
                    ForwardAdvancedGiCombinedContract.SimpleOpaqueFragmentShader,
                    ForwardAdvancedGiCombinedContract
                        .SimpleFullInputOpaqueFragmentShader,
                    "Combined C4 Receiver and C5 Direct Source",
                    out _forwardCombinedAdvancedGiPipeline,
                    out _forwardCompactedCombinedAdvancedGiPipeline,
                    out _forwardSimpleCombinedAdvancedGiPipeline,
                    out _forwardSimpleFullInputCombinedAdvancedGiPipeline,
                    out _forwardCompactedSimpleCombinedAdvancedGiPipeline,
                    out _forwardCompactedSimpleFullInputCombinedAdvancedGiPipeline,
                    secondaryColorFormat:
                        ForwardGiCausticReceiverContract.ReceiverPayloadFormat,
                    tertiaryColorFormat:
                        ForwardNearFieldDirectSourceContract.RequiredAttachmentFormat,
                    quaternaryColorFormat:
                        ForwardNearFieldDirectSourceContract.ReceiverPayloadFormat);

                CreateOpaqueSpecializedPipelineSet(
                    colorFormat,
                    depthFormat,
                    forwardTaskShaderName,
                    ForwardAdvancedGiCombinedContract
                        .ReceiverCacheOpaqueFragmentShader,
                    ForwardAdvancedGiCombinedContract
                        .ReceiverCacheSimpleOpaqueFragmentShader,
                    ForwardAdvancedGiCombinedContract
                        .ReceiverCacheSimpleFullInputOpaqueFragmentShader,
                    "Combined C4/C5 with DDGI Receiver Cache",
                    out _forwardReceiverCacheCombinedAdvancedGiPipeline,
                    out _forwardCompactedReceiverCacheCombinedAdvancedGiPipeline,
                    out _forwardSimpleReceiverCacheCombinedAdvancedGiPipeline,
                    out _forwardSimpleFullInputReceiverCacheCombinedAdvancedGiPipeline,
                    out _forwardCompactedSimpleReceiverCacheCombinedAdvancedGiPipeline,
                    out _forwardCompactedSimpleFullInputReceiverCacheCombinedAdvancedGiPipeline,
                    secondaryColorFormat:
                        ForwardGiCausticReceiverContract.ReceiverPayloadFormat,
                    tertiaryColorFormat:
                        ForwardNearFieldDirectSourceContract.RequiredAttachmentFormat,
                    quaternaryColorFormat:
                        ForwardNearFieldDirectSourceContract.ReceiverPayloadFormat);

                if (_forwardCompactedCombinedAdvancedGiPipeline.Handle == 0 ||
                    _forwardCompactedSimpleCombinedAdvancedGiPipeline.Handle == 0 ||
                    _forwardCompactedSimpleFullInputCombinedAdvancedGiPipeline.Handle == 0 ||
                    _forwardCompactedReceiverCacheCombinedAdvancedGiPipeline.Handle == 0 ||
                    _forwardCompactedSimpleReceiverCacheCombinedAdvancedGiPipeline.Handle == 0 ||
                    _forwardCompactedSimpleFullInputReceiverCacheCombinedAdvancedGiPipeline.Handle == 0 ||
                    (!RendererBuildConfiguration.FastPipelineStartup &&
                     (_forwardCombinedAdvancedGiPipeline.Handle == 0 ||
                      _forwardSimpleCombinedAdvancedGiPipeline.Handle == 0 ||
                      _forwardSimpleFullInputCombinedAdvancedGiPipeline.Handle == 0 ||
                      _forwardReceiverCacheCombinedAdvancedGiPipeline.Handle == 0 ||
                      _forwardSimpleReceiverCacheCombinedAdvancedGiPipeline.Handle == 0 ||
                      _forwardSimpleFullInputReceiverCacheCombinedAdvancedGiPipeline.Handle == 0)))
                {
                    DestroyCombinedAdvancedGiPipelines();
                    CombinedAdvancedGiFailureReason =
                        "combined-advanced-GI-pipeline-variant-incomplete";
                    return;
                }

                CombinedAdvancedGiAttachmentEnabled = true;
                CombinedAdvancedGiFailureReason = "valid";
            }
            catch (Exception ex)
            {
                DestroyCombinedAdvancedGiPipelines();
                CombinedAdvancedGiFailureReason =
                    "combined-advanced-GI-pipeline-creation-failed:" +
                    ex.GetType().Name + ":" + ex.Message;
                System.Diagnostics.Debug.WriteLine(
                    $"Combined C4/C5 forward pipeline unavailable: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void CreateOpaqueSpecializedPipelineSet(
            Format colorFormat,
            Format depthFormat,
            string forwardTaskShaderName,
            string fullFragmentShaderName,
            string simpleFragmentShaderName,
            string simpleFullInputFragmentShaderName,
            string debugVariantName,
            out VkPipeline fullPipeline,
            out VkPipeline compactedPipeline,
            out VkPipeline simplePipeline,
            out VkPipeline simpleFullInputPipeline,
            out VkPipeline compactedSimplePipeline,
            out VkPipeline compactedSimpleFullInputPipeline,
            Format? secondaryColorFormat = null,
            Format? tertiaryColorFormat = null,
            Format? quaternaryColorFormat = null,
            Format? materialTransportProvenanceFormat = null,
            bool depthWriteEnable = false)
        {
            fullPipeline = CreateGraphicsPipeline(
                forwardTaskShaderName,
                "forward.mesh.spv",
                fullFragmentShaderName,
                colorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: depthWriteEnable,
                blendEnable: false,
                cullMode: CullModeFlags.None,
                depthBiasEnable: false,
                secondaryColorFormat: secondaryColorFormat,
                tertiaryColorFormat: tertiaryColorFormat,
                quaternaryColorFormat: quaternaryColorFormat,
                materialTransportProvenanceFormat:
                    materialTransportProvenanceFormat);
            compactedPipeline = CreateGraphicsPipeline(
                taskShaderName: null,
                _compactedForwardMeshShaderName,
                fullFragmentShaderName,
                colorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: depthWriteEnable,
                blendEnable: false,
                cullMode: CullModeFlags.None,
                depthBiasEnable: false,
                secondaryColorFormat: secondaryColorFormat,
                tertiaryColorFormat: tertiaryColorFormat,
                quaternaryColorFormat: quaternaryColorFormat,
                materialTransportProvenanceFormat:
                    materialTransportProvenanceFormat);
            simplePipeline = CreateGraphicsPipeline(
                forwardTaskShaderName,
                "forward_simple.mesh.spv",
                simpleFragmentShaderName,
                colorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: depthWriteEnable,
                blendEnable: false,
                cullMode: CullModeFlags.None,
                depthBiasEnable: false,
                secondaryColorFormat: secondaryColorFormat,
                tertiaryColorFormat: tertiaryColorFormat,
                quaternaryColorFormat: quaternaryColorFormat,
                materialTransportProvenanceFormat:
                    materialTransportProvenanceFormat);
            simpleFullInputPipeline = CreateGraphicsPipeline(
                forwardTaskShaderName,
                "forward.mesh.spv",
                simpleFullInputFragmentShaderName,
                colorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: depthWriteEnable,
                blendEnable: false,
                cullMode: CullModeFlags.None,
                depthBiasEnable: false,
                secondaryColorFormat: secondaryColorFormat,
                tertiaryColorFormat: tertiaryColorFormat,
                quaternaryColorFormat: quaternaryColorFormat,
                materialTransportProvenanceFormat:
                    materialTransportProvenanceFormat);
            compactedSimplePipeline = CreateGraphicsPipeline(
                taskShaderName: null,
                _compactedForwardSimpleMeshShaderName,
                simpleFragmentShaderName,
                colorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: depthWriteEnable,
                blendEnable: false,
                cullMode: CullModeFlags.None,
                depthBiasEnable: false,
                secondaryColorFormat: secondaryColorFormat,
                tertiaryColorFormat: tertiaryColorFormat,
                quaternaryColorFormat: quaternaryColorFormat,
                materialTransportProvenanceFormat:
                    materialTransportProvenanceFormat);
            compactedSimpleFullInputPipeline = CreateGraphicsPipeline(
                taskShaderName: null,
                _compactedForwardMeshShaderName,
                simpleFullInputFragmentShaderName,
                colorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: depthWriteEnable,
                blendEnable: false,
                cullMode: CullModeFlags.None,
                depthBiasEnable: false,
                secondaryColorFormat: secondaryColorFormat,
                tertiaryColorFormat: tertiaryColorFormat,
                quaternaryColorFormat: quaternaryColorFormat,
                materialTransportProvenanceFormat:
                    materialTransportProvenanceFormat);

            _context.SetDebugName(
                fullPipeline.Handle,
                ObjectType.Pipeline,
                $"Opaque Forward Plus {debugVariantName} Mesh Pipeline");
            _context.SetDebugName(
                compactedPipeline.Handle,
                ObjectType.Pipeline,
                $"Compacted Opaque Forward Plus {debugVariantName} Mesh Pipeline");
            _context.SetDebugName(
                simplePipeline.Handle,
                ObjectType.Pipeline,
                $"Simple Opaque Forward Plus {debugVariantName} Mesh Pipeline");
            _context.SetDebugName(
                simpleFullInputPipeline.Handle,
                ObjectType.Pipeline,
                $"Simple Full-Input Opaque Forward Plus {debugVariantName} Mesh Pipeline");
            _context.SetDebugName(
                compactedSimplePipeline.Handle,
                ObjectType.Pipeline,
                $"Compacted Simple Opaque Forward Plus {debugVariantName} Mesh Pipeline");
            _context.SetDebugName(
                compactedSimpleFullInputPipeline.Handle,
                ObjectType.Pipeline,
                $"Compacted Simple Full-Input Opaque Forward Plus {debugVariantName} Mesh Pipeline");
        }

        private void CreateComputePipelines()
        {
            bool resolvedMeshletAddressing =
                Settings.IsPerformanceOptimizationEnabled(
                    PerformanceOptimizationFeature
                        .ResolvedMeshletAddressing);
            string sceneOpaqueCompactionShader = resolvedMeshletAddressing
                ? "scene_opaque_compact.comp.spv"
                : "scene_opaque_compact_virtual.comp.spv";
            _sceneOpaqueCompactionPipeline = CreateComputePipeline(
                sceneOpaqueCompactionShader,
                _sceneSubmissionComputeLayout);
            _context.SetDebugName(_sceneOpaqueCompactionPipeline.Handle, ObjectType.Pipeline, "Scene Opaque Compaction Compute Pipeline");
            if (GpuMeshletCountersEnabled)
            {
                _sceneOpaqueCompactionDiagnosticsPipeline = CreateComputePipeline(
                    resolvedMeshletAddressing
                        ? "scene_opaque_compact_diagnostics.comp.spv"
                        : "scene_opaque_compact_virtual_diagnostics.comp.spv",
                    _sceneSubmissionComputeLayout);
                _context.SetDebugName(
                    _sceneOpaqueCompactionDiagnosticsPipeline.Handle,
                    ObjectType.Pipeline,
                    "Scene Opaque Compaction Exact Shadow Diagnostics Pipeline");
            }
            _forwardVisibilityCompactionPipeline = CreateComputePipeline("forward_visibility_compact.comp.spv", _sceneSubmissionComputeLayout);
            _context.SetDebugName(_forwardVisibilityCompactionPipeline.Handle, ObjectType.Pipeline, "Forward Visibility Compaction Compute Pipeline");
        }

        internal static bool UsesDynamicRasterState(
            string meshShaderName,
            bool hasColorAttachment,
            bool blendEnable)
        {
            if (hasColorAttachment)
            {
                return !blendEnable && meshShaderName.StartsWith(
                    "forward",
                    StringComparison.Ordinal);
            }

            return meshShaderName.Contains(
                       "compacted",
                       StringComparison.Ordinal) &&
                   (meshShaderName.StartsWith(
                        "depth",
                        StringComparison.Ordinal) ||
                    meshShaderName.StartsWith(
                        "shadow_depth",
                        StringComparison.Ordinal));
        }

        private VkPipeline CreateGraphicsPipeline(
            string? taskShaderName,
            string meshShaderName,
            string? fragmentShaderName,
            Format colorFormat,
            Format depthFormat,
            bool hasColorAttachment,
            bool depthWriteEnable,
            bool blendEnable,
            CullModeFlags cullMode,
            bool depthBiasEnable,
            Format? secondaryColorFormat = null,
            Format? tertiaryColorFormat = null,
            Format? quaternaryColorFormat = null,
            Format? quinaryColorFormat = null,
            Format? senaryColorFormat = null,
            Format? materialTransportProvenanceFormat = null,
            PipelineLayout pipelineLayout = default,
            bool hybridReflectionReceiverEnabled = false,
            bool destinationColorModulationBlend = false,
            uint forwardReceiverCacheLane =
                ForwardReceiverCacheCombinedLane)
        {
            ShaderModule taskModule = new ShaderModule();
            ShaderModule meshModule = new ShaderModule();
            ShaderModule fragmentModule = new ShaderModule();
            bool usesPerformanceSpecialization =
                UsesForwardPerformanceSpecialization(fragmentShaderName);
            uint performanceSpecializationMask =
                ResolveForwardPerformanceSpecializationMask(Settings);
            string performanceIdentity = usesPerformanceSpecialization
                ? $".receiver-lane-{forwardReceiverCacheLane}.performance-{performanceSpecializationMask:x8}"
                : string.Empty;

            try
            {
                if (taskShaderName != null)
                {
                    taskModule = ShaderModuleLoader.Load(_context, taskShaderName);
                    _context.SetDebugName(taskModule.Handle, ObjectType.ShaderModule, taskShaderName);
                }
                meshModule = ShaderModuleLoader.Load(_context, meshShaderName);
                _context.SetDebugName(meshModule.Handle, ObjectType.ShaderModule, meshShaderName);
                if (fragmentShaderName != null)
                {
                    fragmentModule = ShaderModuleLoader.Load(_context, fragmentShaderName);
                    _context.SetDebugName(fragmentModule.Handle, ObjectType.ShaderModule, fragmentShaderName);
                }

                return CreateTrackedPipeline(
                    $"MeshPipeline.Graphics.{fragmentShaderName ?? "depth-only"}.{meshShaderName}.{taskShaderName ?? "no-task"}{performanceIdentity}",
                    artifactId => CreateGraphicsPipeline(
                        taskModule,
                        meshModule,
                        fragmentModule,
                        colorFormat,
                        depthFormat,
                        hasColorAttachment,
                        depthWriteEnable,
                        blendEnable,
                        cullMode,
                        depthBiasEnable,
                        secondaryColorFormat: secondaryColorFormat,
                        tertiaryColorFormat: tertiaryColorFormat,
                        quaternaryColorFormat: quaternaryColorFormat,
                        quinaryColorFormat: quinaryColorFormat,
                        senaryColorFormat: senaryColorFormat,
                        materialTransportProvenanceFormat:
                            materialTransportProvenanceFormat,
                        pipelineLayout: pipelineLayout,
                        hybridReflectionReceiverEnabled:
                            hybridReflectionReceiverEnabled,
                        destinationColorModulationBlend:
                            destinationColorModulationBlend,
                        dynamicRasterState: UsesDynamicRasterState(
                            meshShaderName,
                            hasColorAttachment,
                            blendEnable),
                        fragmentPerformanceSpecialization:
                            usesPerformanceSpecialization,
                        fragmentPerformanceOptimizationMask:
                            performanceSpecializationMask,
                        fragmentReceiverCacheLane:
                            forwardReceiverCacheLane,
                        artifactId));
            }
            catch (Exception exception)
            {
                string detail =
                    "Failed to create graphics pipeline " +
                    $"(task='{taskShaderName ?? "none"}', " +
                    $"mesh='{meshShaderName}', " +
                    $"fragment='{fragmentShaderName ?? "none"}'): " +
                    $"{exception.GetType().Name}: {exception.Message}";
                throw new InvalidOperationException(detail, exception);
            }
            finally
            {
                DestroyShaderModule(fragmentModule);
                DestroyShaderModule(meshModule);
                DestroyShaderModule(taskModule);
            }
        }

        private VkPipeline CreateComputePipeline(string shaderName, PipelineLayout layout)
        {
            return CreateTrackedPipeline(
                $"MeshPipeline.Compute.{shaderName}",
                artifactId => CreateComputePipelineCore(
                    shaderName,
                    layout,
                    artifactId));
        }

        private VkPipeline CreateComputePipelineCore(
            string shaderName,
            PipelineLayout layout,
            PipelineArtifactId artifactId)
        {
            ShaderModule shaderModule = default;
            try
            {
                shaderModule = ShaderModuleLoader.Load(_context, shaderName);
                _context.SetDebugName(shaderModule.Handle, ObjectType.ShaderModule, shaderName);

                var shaderStageInfo = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = shaderModule,
                    PName = (byte*)_entryPointName
                };

                var pipelineInfo = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Stage = shaderStageInfo,
                    Layout = layout,
                    BasePipelineHandle = default,
                    BasePipelineIndex = -1
                };

                Result result = _pipelineCacheService != null
                    ? _pipelineCacheService.CreateComputePipeline(
                        artifactId,
                        &pipelineInfo,
                        out VkPipeline pipeline)
                    : _context.Api.CreateComputePipelines(
                        _context.Device,
                        _pipelineCache,
                        1,
                        &pipelineInfo,
                        null,
                        out pipeline);

                if (result != Result.Success)
                    throw new VulkanException("Failed to create mesh compute pipeline", result);

                return pipeline;
            }
            finally
            {
                DestroyShaderModule(shaderModule);
            }
        }

        private VkPipeline CreateWeightedOitGraphicsPipeline(
            string? taskShaderName,
            string meshShaderName,
            string fragmentShaderName,
            Format accumulationFormat,
            Format revealageFormat,
            Format depthFormat,
            PipelineLayout pipelineLayout = default)
        {
            ShaderModule taskModule = new ShaderModule();
            ShaderModule meshModule = new ShaderModule();
            ShaderModule fragmentModule = new ShaderModule();
            bool usesPerformanceSpecialization =
                UsesForwardPerformanceSpecialization(fragmentShaderName);
            uint performanceSpecializationMask =
                ResolveForwardPerformanceSpecializationMask(Settings);
            string performanceIdentity = usesPerformanceSpecialization
                ? $".performance-{performanceSpecializationMask:x8}"
                : string.Empty;

            try
            {
                if (taskShaderName != null)
                {
                    taskModule = ShaderModuleLoader.Load(
                        _context,
                        taskShaderName);
                    _context.SetDebugName(
                        taskModule.Handle,
                        ObjectType.ShaderModule,
                        taskShaderName);
                }
                meshModule = ShaderModuleLoader.Load(_context, meshShaderName);
                _context.SetDebugName(meshModule.Handle, ObjectType.ShaderModule, meshShaderName);
                fragmentModule = ShaderModuleLoader.Load(_context, fragmentShaderName);
                _context.SetDebugName(fragmentModule.Handle, ObjectType.ShaderModule, fragmentShaderName);

                return CreateTrackedPipeline(
                    $"MeshPipeline.Graphics.{fragmentShaderName}.{meshShaderName}.{taskShaderName ?? "no-task"}{performanceIdentity}",
                    artifactId => CreateWeightedOitGraphicsPipeline(
                        taskModule,
                        meshModule,
                        fragmentModule,
                        accumulationFormat,
                        revealageFormat,
                        depthFormat,
                        pipelineLayout,
                        usesPerformanceSpecialization,
                        performanceSpecializationMask,
                        artifactId));
            }
            finally
            {
                DestroyShaderModule(fragmentModule);
                DestroyShaderModule(meshModule);
                DestroyShaderModule(taskModule);
            }
        }

        private VkPipeline CreateGraphicsPipeline(
            ShaderModule taskModule,
            ShaderModule meshModule,
            ShaderModule fragmentModule,
            Format colorFormat,
            Format depthFormat,
            bool hasColorAttachment,
            bool depthWriteEnable,
            bool blendEnable,
            CullModeFlags cullMode,
            bool depthBiasEnable,
            Format? secondaryColorFormat = null,
            Format? tertiaryColorFormat = null,
            Format? quaternaryColorFormat = null,
            Format? quinaryColorFormat = null,
            Format? senaryColorFormat = null,
            Format? materialTransportProvenanceFormat = null,
            PipelineLayout pipelineLayout = default,
            bool hybridReflectionReceiverEnabled = false,
            bool destinationColorModulationBlend = false,
            PipelineArtifactId artifactId = default)
            => CreateGraphicsPipeline(
                taskModule,
                meshModule,
                fragmentModule,
                colorFormat,
                depthFormat,
                hasColorAttachment,
                depthWriteEnable,
                blendEnable,
                cullMode,
                depthBiasEnable,
                secondaryColorFormat,
                tertiaryColorFormat,
                quaternaryColorFormat,
                quinaryColorFormat,
                senaryColorFormat,
                materialTransportProvenanceFormat,
                pipelineLayout,
                hybridReflectionReceiverEnabled,
                destinationColorModulationBlend,
                dynamicRasterState: false,
                fragmentPerformanceSpecialization: false,
                fragmentPerformanceOptimizationMask: 0u,
                fragmentReceiverCacheLane:
                    ForwardReceiverCacheCombinedLane,
                artifactId);

        private VkPipeline CreateGraphicsPipeline(
            ShaderModule taskModule,
            ShaderModule meshModule,
            ShaderModule fragmentModule,
            Format colorFormat,
            Format depthFormat,
            bool hasColorAttachment,
            bool depthWriteEnable,
            bool blendEnable,
            CullModeFlags cullMode,
            bool depthBiasEnable,
            Format? secondaryColorFormat,
            Format? tertiaryColorFormat,
            Format? quaternaryColorFormat,
            Format? quinaryColorFormat,
            Format? senaryColorFormat,
            Format? materialTransportProvenanceFormat,
            PipelineLayout pipelineLayout,
            bool hybridReflectionReceiverEnabled,
            bool destinationColorModulationBlend,
            bool dynamicRasterState,
            bool fragmentPerformanceSpecialization,
            uint fragmentPerformanceOptimizationMask,
            uint fragmentReceiverCacheLane,
            PipelineArtifactId artifactId)
        {
            var specializationData = stackalloc uint[2];
            specializationData[0] = fragmentReceiverCacheLane;
            specializationData[1] = fragmentPerformanceOptimizationMask;
            var specializationEntries =
                stackalloc SpecializationMapEntry[2];
            specializationEntries[0] = new SpecializationMapEntry
            {
                ConstantID =
                    ForwardReceiverCacheLaneSpecializationConstantId,
                Offset = 0u,
                Size = (nuint)sizeof(uint)
            };
            specializationEntries[1] = new SpecializationMapEntry
            {
                ConstantID = ForwardPerformanceSpecializationConstantId,
                Offset = (uint)sizeof(uint),
                Size = (nuint)sizeof(uint)
            };
            var specializationInfo = new SpecializationInfo
            {
                MapEntryCount = 2u,
                PMapEntries = specializationEntries,
                DataSize = (nuint)(2 * sizeof(uint)),
                PData = specializationData
            };
            var stages = stackalloc PipelineShaderStageCreateInfo[3];
            int stageCount = 0;
            if (taskModule.Handle != 0)
                stages[stageCount++] = CreateShaderStageInfo(ShaderStageFlags.TaskBitExt, taskModule);
            stages[stageCount++] = CreateShaderStageInfo(ShaderStageFlags.MeshBitExt, meshModule);
            if (fragmentModule.Handle != 0)
            {
                PipelineShaderStageCreateInfo fragmentStage =
                    CreateShaderStageInfo(
                        ShaderStageFlags.FragmentBit,
                        fragmentModule);
                if (fragmentPerformanceSpecialization)
                    fragmentStage.PSpecializationInfo = &specializationInfo;
                stages[stageCount++] = fragmentStage;
            }

            var vertexInputInfo = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo
            };

            var inputAssemblyInfo = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList
            };

            var viewportInfo = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                ScissorCount = 1
            };

            var rasterInfo = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                DepthClampEnable = false,
                RasterizerDiscardEnable = false,
                PolygonMode = PolygonMode.Fill,
                CullMode = cullMode,
                // Projection matrices flip clip-space Y for Vulkan's positive-height
                // viewport, so imported glTF CCW winding remains CCW at rasterization.
                FrontFace = FrontFace.CounterClockwise,
                DepthBiasEnable = depthBiasEnable,
                LineWidth = 1.0f
            };

            var multisampleInfo = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit
            };

            var depthStencilInfo = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = true,
                DepthWriteEnable = depthWriteEnable,
                DepthCompareOp = CompareOp.GreaterOrEqual,
                DepthBoundsTestEnable = false,
                StencilTestEnable = false,
                MinDepthBounds = 0.0f,
                MaxDepthBounds = 1.0f
            };

            var colorBlendAttachment = new PipelineColorBlendAttachmentState
            {
                BlendEnable = blendEnable,
                SrcColorBlendFactor = destinationColorModulationBlend
                    ? BlendFactor.DstColor
                    : blendEnable ? BlendFactor.SrcAlpha : BlendFactor.One,
                DstColorBlendFactor = blendEnable ? BlendFactor.OneMinusSrcAlpha : BlendFactor.Zero,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = blendEnable ? BlendFactor.OneMinusSrcAlpha : BlendFactor.Zero,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = ColorComponentFlags.RBit |
                                 ColorComponentFlags.GBit |
                                 ColorComponentFlags.BBit |
                                 ColorComponentFlags.ABit
            };
            if ((secondaryColorFormat.HasValue || tertiaryColorFormat.HasValue ||
                    quaternaryColorFormat.HasValue || quinaryColorFormat.HasValue ||
                    senaryColorFormat.HasValue) &&
                materialTransportProvenanceFormat.HasValue)
            {
                throw new InvalidOperationException(
                    "A mesh pipeline cannot bind material provenance with a C4 or C5 experimental MRT output.");
            }
            if (tertiaryColorFormat.HasValue && !secondaryColorFormat.HasValue)
            {
                throw new InvalidOperationException(
                    "A tertiary forward attachment requires a secondary attachment.");
            }
            if (quaternaryColorFormat.HasValue &&
                (!secondaryColorFormat.HasValue || !tertiaryColorFormat.HasValue))
            {
                throw new InvalidOperationException(
                    "A quaternary forward attachment requires contiguous secondary and tertiary attachments.");
            }
            if (quinaryColorFormat.HasValue &&
                (!secondaryColorFormat.HasValue || !tertiaryColorFormat.HasValue ||
                 !quaternaryColorFormat.HasValue))
            {
                throw new InvalidOperationException(
                    "A fifth forward attachment requires four contiguous preceding attachments.");
            }
            if (senaryColorFormat.HasValue &&
                (!secondaryColorFormat.HasValue || !tertiaryColorFormat.HasValue ||
                 !quaternaryColorFormat.HasValue || !quinaryColorFormat.HasValue))
            {
                throw new InvalidOperationException(
                    "A sixth forward attachment requires five contiguous preceding attachments.");
            }

            uint colorAttachmentCount = hybridReflectionReceiverEnabled
                ? senaryColorFormat.HasValue ? 6u
                : quinaryColorFormat.HasValue ? 5u
                : quaternaryColorFormat.HasValue ? 4u
                : tertiaryColorFormat.HasValue ? 3u
                : secondaryColorFormat.HasValue ? 2u
                : throw new InvalidOperationException(
                    "A hybrid reflection pipeline requires its receiver attachment.")
                : ForwardDynamicRenderingContract.ResolveColorAttachmentCount(
                    hasColorAttachment,
                    materialTransportProvenanceFormat.HasValue,
                    nearFieldDirectSourceEnabled: tertiaryColorFormat.HasValue,
                    giCausticReceiverEnabled:
                        secondaryColorFormat.HasValue &&
                        (!tertiaryColorFormat.HasValue ||
                         quaternaryColorFormat.HasValue));
            var colorBlendAttachments = stackalloc PipelineColorBlendAttachmentState[6];
            for (int attachmentIndex = 0;
                 attachmentIndex <
                    6;
                 attachmentIndex++)
            {
                colorBlendAttachments[attachmentIndex] = colorBlendAttachment;
            }

            var colorBlendInfo = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                LogicOpEnable = false,
                AttachmentCount = colorAttachmentCount,
                PAttachments = colorAttachmentCount > 0 ? colorBlendAttachments : null
            };

            var dynamicStates = stackalloc DynamicState[5];
            dynamicStates[0] = DynamicState.Viewport;
            dynamicStates[1] = DynamicState.Scissor;
            dynamicStates[2] = DynamicState.DepthBias;
            uint dynamicStateCount = 3;
            if (dynamicRasterState)
            {
                dynamicStates[dynamicStateCount++] = DynamicState.CullMode;
                dynamicStates[dynamicStateCount++] =
                    DynamicState.DepthCompareOp;
            }

            var dynamicInfo = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = dynamicStateCount,
                PDynamicStates = dynamicStates
            };

            var renderingColorFormats = stackalloc Format[6];
            renderingColorFormats[0] = colorFormat;
            renderingColorFormats[1] =
                secondaryColorFormat ??
                materialTransportProvenanceFormat ??
                colorFormat;
            renderingColorFormats[2] =
                tertiaryColorFormat ?? colorFormat;
            renderingColorFormats[3] =
                quaternaryColorFormat ?? colorFormat;
            renderingColorFormats[4] =
                quinaryColorFormat ?? colorFormat;
            renderingColorFormats[5] =
                senaryColorFormat ?? colorFormat;
            var renderingInfo = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = colorAttachmentCount,
                PColorAttachmentFormats = colorAttachmentCount > 0 ? renderingColorFormats : null,
                DepthAttachmentFormat = depthFormat,
                StencilAttachmentFormat = Format.Undefined
            };
            var fragmentShadingRateState =
                new PipelineFragmentShadingRateStateCreateInfoKHR
                {
                    SType = StructureType
                        .PipelineFragmentShadingRateStateCreateInfoKhr,
                    FragmentSize = new Extent2D
                    {
                        Width = 1,
                        Height = 1
                    }
                };
            fragmentShadingRateState.CombinerOps.Element0 =
                FragmentShadingRateCombinerOpKHR.KeepKhr;
            fragmentShadingRateState.CombinerOps.Element1 =
                FragmentShadingRateCombinerOpKHR.ReplaceKhr;
            if (_context.FragmentShadingRateSupported)
                renderingInfo.PNext = &fragmentShadingRateState;

            var pipelineInfo = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = &renderingInfo,
                StageCount = checked((uint)stageCount),
                PStages = stages,
                PVertexInputState = &vertexInputInfo,
                PInputAssemblyState = &inputAssemblyInfo,
                PViewportState = &viewportInfo,
                PRasterizationState = &rasterInfo,
                PMultisampleState = &multisampleInfo,
                PDepthStencilState = &depthStencilInfo,
                PColorBlendState = &colorBlendInfo,
                PDynamicState = &dynamicInfo,
                Layout = pipelineLayout.Handle != 0 ? pipelineLayout : _layout,
                RenderPass = default,
                Subpass = 0,
                BasePipelineHandle = default,
                BasePipelineIndex = -1
            };

            Result result = _pipelineCacheService != null
                ? _pipelineCacheService.CreateGraphicsPipeline(
                    artifactId,
                    &pipelineInfo,
                    out VkPipeline pipeline)
                : _context.Api.CreateGraphicsPipelines(
                    _context.Device,
                    _pipelineCache,
                    1,
                    &pipelineInfo,
                    null,
                    out pipeline);

            if (result != Result.Success)
                throw new VulkanException("Failed to create mesh graphics pipeline", result);

            return pipeline;
        }

        private VkPipeline CreateWeightedOitGraphicsPipeline(
            ShaderModule taskModule,
            ShaderModule meshModule,
            ShaderModule fragmentModule,
            Format accumulationFormat,
            Format revealageFormat,
            Format depthFormat,
            PipelineLayout pipelineLayout,
            bool fragmentPerformanceSpecialization,
            uint fragmentPerformanceOptimizationMask,
            PipelineArtifactId artifactId)
        {
            uint specializationData =
                fragmentPerformanceOptimizationMask;
            var specializationEntry = new SpecializationMapEntry
            {
                ConstantID = ForwardPerformanceSpecializationConstantId,
                Offset = 0u,
                Size = (nuint)sizeof(uint)
            };
            var specializationInfo = new SpecializationInfo
            {
                MapEntryCount = 1u,
                PMapEntries = &specializationEntry,
                DataSize = (nuint)sizeof(uint),
                PData = &specializationData
            };
            var stages = stackalloc PipelineShaderStageCreateInfo[3];
            int stageCount = 0;
            if (taskModule.Handle != 0)
                stages[stageCount++] = CreateShaderStageInfo(ShaderStageFlags.TaskBitExt, taskModule);
            stages[stageCount++] = CreateShaderStageInfo(ShaderStageFlags.MeshBitExt, meshModule);
            PipelineShaderStageCreateInfo fragmentStage =
                CreateShaderStageInfo(
                    ShaderStageFlags.FragmentBit,
                    fragmentModule);
            if (fragmentPerformanceSpecialization)
                fragmentStage.PSpecializationInfo = &specializationInfo;
            stages[stageCount++] = fragmentStage;

            var vertexInputInfo = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo
            };

            var inputAssemblyInfo = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList
            };

            var viewportInfo = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                ScissorCount = 1
            };

            var rasterInfo = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                DepthClampEnable = false,
                RasterizerDiscardEnable = false,
                PolygonMode = PolygonMode.Fill,
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise,
                DepthBiasEnable = false,
                LineWidth = 1.0f
            };

            var multisampleInfo = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit
            };

            var depthStencilInfo = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = true,
                DepthWriteEnable = false,
                DepthCompareOp = CompareOp.GreaterOrEqual,
                DepthBoundsTestEnable = false,
                StencilTestEnable = false,
                MinDepthBounds = 0.0f,
                MaxDepthBounds = 1.0f
            };

            var colorBlendAttachments = stackalloc PipelineColorBlendAttachmentState[2];
            colorBlendAttachments[0] = new PipelineColorBlendAttachmentState
            {
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.One,
                DstColorBlendFactor = BlendFactor.One,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.One,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = ColorComponentFlags.RBit |
                                 ColorComponentFlags.GBit |
                                 ColorComponentFlags.BBit |
                                 ColorComponentFlags.ABit
            };
            colorBlendAttachments[1] = new PipelineColorBlendAttachmentState
            {
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.One,
                DstColorBlendFactor = BlendFactor.One,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.One,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = ColorComponentFlags.RBit |
                                 ColorComponentFlags.GBit |
                                 ColorComponentFlags.BBit |
                                 ColorComponentFlags.ABit
            };

            var colorBlendInfo = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                LogicOpEnable = false,
                AttachmentCount = 2,
                PAttachments = colorBlendAttachments
            };

            var dynamicStates = stackalloc DynamicState[3];
            dynamicStates[0] = DynamicState.Viewport;
            dynamicStates[1] = DynamicState.Scissor;
            dynamicStates[2] = DynamicState.DepthBias;

            var dynamicInfo = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 3,
                PDynamicStates = dynamicStates
            };

            var colorFormats = stackalloc Format[2];
            colorFormats[0] = accumulationFormat;
            colorFormats[1] = revealageFormat;
            var renderingInfo = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = 2,
                PColorAttachmentFormats = colorFormats,
                DepthAttachmentFormat = depthFormat,
                StencilAttachmentFormat = Format.Undefined
            };

            var pipelineInfo = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = &renderingInfo,
                StageCount = checked((uint)stageCount),
                PStages = stages,
                PVertexInputState = &vertexInputInfo,
                PInputAssemblyState = &inputAssemblyInfo,
                PViewportState = &viewportInfo,
                PRasterizationState = &rasterInfo,
                PMultisampleState = &multisampleInfo,
                PDepthStencilState = &depthStencilInfo,
                PColorBlendState = &colorBlendInfo,
                PDynamicState = &dynamicInfo,
                Layout = pipelineLayout.Handle != 0 ? pipelineLayout : _layout,
                RenderPass = default,
                Subpass = 0,
                BasePipelineHandle = default,
                BasePipelineIndex = -1
            };

            Result result = _pipelineCacheService != null
                ? _pipelineCacheService.CreateGraphicsPipeline(
                    artifactId,
                    &pipelineInfo,
                    out VkPipeline pipeline)
                : _context.Api.CreateGraphicsPipelines(
                    _context.Device,
                    _pipelineCache,
                    1,
                    &pipelineInfo,
                    null,
                    out pipeline);

            if (result != Result.Success)
                throw new VulkanException("Failed to create weighted OIT mesh graphics pipeline", result);

            return pipeline;
        }

        private PipelineShaderStageCreateInfo CreateShaderStageInfo(ShaderStageFlags stageFlags, ShaderModule module)
        {
            return new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = stageFlags,
                Module = module,
                PName = (byte*)_entryPointName
            };
        }

        private void RunStartupStep(string name, Action action)
        {
            if (_runStartupStep == null)
            {
                action();
                return;
            }

            _runStartupStep(name, action);
        }

        private void RunPipelineCreationBatch(
            params (string Name, Action Compile)[] work)
        {
            if (_pipelineCacheService == null || work.Length < 2 ||
                _pipelineCacheService.CompilationScheduler.WorkerCount == 1)
            {
                foreach ((_, Action compile) in work)
                    compile();
                return;
            }

            int generation = System.Threading.Interlocked.Increment(
                ref _pipelineCompilationBatchGeneration);
            var manifest = new PipelineStartupManifest(
                $"mesh-startup-{generation}");
            foreach ((string name, Action compile) in work)
            {
                var artifactId = new PipelineArtifactId(
                    $"MeshPipeline.Batch.{generation}.{name}");
                manifest.Require(artifactId);
                _pipelineCacheService.CompilationScheduler.Schedule(
                    artifactId,
                    _ => compile());
            }

            _pipelineCacheService.CompilationScheduler.Wait(manifest);
        }

        private VkPipeline CreateTrackedPipeline(
            string name,
            Func<PipelineArtifactId, VkPipeline> create)
        {
            VkPipeline pipeline = default;
            RunStartupStep(name, () =>
            {
                pipeline = create(new PipelineArtifactId(name));
            });
            return pipeline;
        }

        private void DestroyPipelines()
        {
            InvalidateForwardOpaquePipelineCache();
            DestroyTransparentPartitionPipelines();
            ResetDeferredPipelineStates();
            NearFieldDirectSourceAttachmentEnabled = false;
            GiCausticReceiverAttachmentEnabled = false;
            CombinedAdvancedGiAttachmentEnabled = false;
            HybridReflectionAttachmentEnabled = false;
            DestroyNearFieldDirectSourcePipelines();
            DestroyGiCausticReceiverPipelines();
            DestroyCombinedAdvancedGiPipelines();
            DestroyHybridReflectionPipelines();
            DestroyAlphaMaskReceiverFeedbackPipelines();
            DestroyTransparentReceiverFeedbackPipelines();

            if (_depthPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _depthPipeline, null);
                _depthPipeline = default;
            }

            if (_shadowDepthPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _shadowDepthPipeline, null);
                _shadowDepthPipeline = default;
            }

            if (_maskedDepthPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _maskedDepthPipeline, null);
                _maskedDepthPipeline = default;
            }

            if (_compactedDepthPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(
                    _context.Device,
                    _compactedDepthPipeline,
                    null);
                _compactedDepthPipeline = default;
            }

            if (_compactedMaskedDepthPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(
                    _context.Device,
                    _compactedMaskedDepthPipeline,
                    null);
                _compactedMaskedDepthPipeline = default;
            }

            if (_shadowAlphaDepthPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _shadowAlphaDepthPipeline, null);
                _shadowAlphaDepthPipeline = default;
            }

            if (_compactedShadowAlphaDepthPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(
                    _context.Device,
                    _compactedShadowAlphaDepthPipeline,
                    null);
                _compactedShadowAlphaDepthPipeline = default;
            }

            if (_forwardPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _forwardPipeline, null);
                _forwardPipeline = default;
            }

            if (_forwardCompactedPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _forwardCompactedPipeline, null);
                _forwardCompactedPipeline = default;
            }

            if (_forwardSimplePipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _forwardSimplePipeline, null);
                _forwardSimplePipeline = default;
            }

            if (_forwardSimpleFullInputPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _forwardSimpleFullInputPipeline, null);
                _forwardSimpleFullInputPipeline = default;
            }

            if (_forwardCompactedSimplePipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _forwardCompactedSimplePipeline, null);
                _forwardCompactedSimplePipeline = default;
            }

            if (_forwardCompactedSimpleFullInputPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _forwardCompactedSimpleFullInputPipeline, null);
                _forwardCompactedSimpleFullInputPipeline = default;
            }

            if (_forwardReceiverCachePipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _forwardReceiverCachePipeline, null);
                _forwardReceiverCachePipeline = default;
            }

            if (_forwardCompactedReceiverCachePipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _forwardCompactedReceiverCachePipeline, null);
                _forwardCompactedReceiverCachePipeline = default;
            }

            if (_forwardSimpleReceiverCachePipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _forwardSimpleReceiverCachePipeline, null);
                _forwardSimpleReceiverCachePipeline = default;
            }

            if (_forwardSimpleFullInputReceiverCachePipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _forwardSimpleFullInputReceiverCachePipeline, null);
                _forwardSimpleFullInputReceiverCachePipeline = default;
            }

            if (_forwardCompactedSimpleReceiverCachePipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _forwardCompactedSimpleReceiverCachePipeline, null);
                _forwardCompactedSimpleReceiverCachePipeline = default;
            }

            if (_forwardCompactedSimpleFullInputReceiverCachePipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(
                    _context.Device,
                    _forwardCompactedSimpleFullInputReceiverCachePipeline,
                    null);
                _forwardCompactedSimpleFullInputReceiverCachePipeline = default;
            }

            DestroyOptionalPipeline(ref _forwardReceiverCacheLegacyPipeline);
            DestroyOptionalPipeline(ref _forwardCompactedReceiverCacheLegacyPipeline);
            DestroyOptionalPipeline(ref _forwardSimpleReceiverCacheLegacyPipeline);
            DestroyOptionalPipeline(ref _forwardSimpleFullInputReceiverCacheLegacyPipeline);
            DestroyOptionalPipeline(ref _forwardCompactedSimpleReceiverCacheLegacyPipeline);
            DestroyOptionalPipeline(
                ref _forwardCompactedSimpleFullInputReceiverCacheLegacyPipeline);
            DestroyOptionalPipeline(ref _forwardReceiverCacheDebugPipeline);
            DestroyOptionalPipeline(ref _forwardCompactedReceiverCacheDebugPipeline);
            DestroyOptionalPipeline(ref _forwardSimpleReceiverCacheDebugPipeline);
            DestroyOptionalPipeline(ref _forwardSimpleFullInputReceiverCacheDebugPipeline);
            DestroyOptionalPipeline(ref _forwardCompactedSimpleReceiverCacheDebugPipeline);
            DestroyOptionalPipeline(
                ref _forwardCompactedSimpleFullInputReceiverCacheDebugPipeline);

            if (_forwardReceiverCacheDiagnosticsPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(
                    _context.Device,
                    _forwardReceiverCacheDiagnosticsPipeline,
                    null);
                _forwardReceiverCacheDiagnosticsPipeline = default;
            }
            if (_forwardCompactedReceiverCacheDiagnosticsPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(
                    _context.Device,
                    _forwardCompactedReceiverCacheDiagnosticsPipeline,
                    null);
                _forwardCompactedReceiverCacheDiagnosticsPipeline = default;
            }
            if (_forwardSimpleReceiverCacheDiagnosticsPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(
                    _context.Device,
                    _forwardSimpleReceiverCacheDiagnosticsPipeline,
                    null);
                _forwardSimpleReceiverCacheDiagnosticsPipeline = default;
            }
            if (_forwardSimpleFullInputReceiverCacheDiagnosticsPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(
                    _context.Device,
                    _forwardSimpleFullInputReceiverCacheDiagnosticsPipeline,
                    null);
                _forwardSimpleFullInputReceiverCacheDiagnosticsPipeline = default;
            }
            if (_forwardCompactedSimpleReceiverCacheDiagnosticsPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(
                    _context.Device,
                    _forwardCompactedSimpleReceiverCacheDiagnosticsPipeline,
                    null);
                _forwardCompactedSimpleReceiverCacheDiagnosticsPipeline = default;
            }
            if (_forwardCompactedSimpleFullInputReceiverCacheDiagnosticsPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(
                    _context.Device,
                    _forwardCompactedSimpleFullInputReceiverCacheDiagnosticsPipeline,
                    null);
                _forwardCompactedSimpleFullInputReceiverCacheDiagnosticsPipeline =
                    default;
            }

            if (_forwardGiDisabledPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _forwardGiDisabledPipeline, null);
                _forwardGiDisabledPipeline = default;
            }

            if (_forwardCompactedGiDisabledPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _forwardCompactedGiDisabledPipeline, null);
                _forwardCompactedGiDisabledPipeline = default;
            }

            if (_forwardSimpleGiDisabledPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _forwardSimpleGiDisabledPipeline, null);
                _forwardSimpleGiDisabledPipeline = default;
            }

            if (_forwardSimpleFullInputGiDisabledPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _forwardSimpleFullInputGiDisabledPipeline, null);
                _forwardSimpleFullInputGiDisabledPipeline = default;
            }

            if (_forwardCompactedSimpleGiDisabledPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _forwardCompactedSimpleGiDisabledPipeline, null);
                _forwardCompactedSimpleGiDisabledPipeline = default;
            }

            if (_forwardCompactedSimpleFullInputGiDisabledPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(
                    _context.Device,
                    _forwardCompactedSimpleFullInputGiDisabledPipeline,
                    null);
                _forwardCompactedSimpleFullInputGiDisabledPipeline = default;
            }

            if (_transparentForwardPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _transparentForwardPipeline, null);
                _transparentForwardPipeline = default;
            }

            if (_thinGlassForwardPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(
                    _context.Device,
                    _thinGlassForwardPipeline,
                    null);
                _thinGlassForwardPipeline = default;
            }

            if (_geometryDecalOverlayPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(
                    _context.Device,
                    _geometryDecalOverlayPipeline,
                    null);
                _geometryDecalOverlayPipeline = default;
            }

            if (_weightedOitTransparentPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _weightedOitTransparentPipeline, null);
                _weightedOitTransparentPipeline = default;
            }
            DestroyOptionalPipeline(ref _rayTransparentForwardPipeline);
            DestroyOptionalPipeline(ref _rayWeightedOitTransparentPipeline);

            if (_motionVectorPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _motionVectorPipeline, null);
                _motionVectorPipeline = default;
            }

            if (_maskedMotionVectorPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _maskedMotionVectorPipeline, null);
                _maskedMotionVectorPipeline = default;
            }

            if (_compactedMotionVectorPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(
                    _context.Device,
                    _compactedMotionVectorPipeline,
                    null);
                _compactedMotionVectorPipeline = default;
            }

            if (_compactedMaskedMotionVectorPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(
                    _context.Device,
                    _compactedMaskedMotionVectorPipeline,
                    null);
                _compactedMaskedMotionVectorPipeline = default;
            }

            if (_sceneOpaqueCompactionPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _sceneOpaqueCompactionPipeline, null);
                _sceneOpaqueCompactionPipeline = default;
            }

            if (_sceneOpaqueCompactionDiagnosticsPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _sceneOpaqueCompactionDiagnosticsPipeline, null);
                _sceneOpaqueCompactionDiagnosticsPipeline = default;
            }

            if (_forwardVisibilityCompactionPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _forwardVisibilityCompactionPipeline, null);
                _forwardVisibilityCompactionPipeline = default;
            }
        }

        private void DestroyNearFieldDirectSourcePipelines()
        {
            DestroyOptionalPipeline(ref _forwardNearFieldDirectSourcePipeline);
            DestroyOptionalPipeline(
                ref _forwardCompactedNearFieldDirectSourcePipeline);
            DestroyOptionalPipeline(
                ref _forwardSimpleNearFieldDirectSourcePipeline);
            DestroyOptionalPipeline(
                ref _forwardSimpleFullInputNearFieldDirectSourcePipeline);
            DestroyOptionalPipeline(
                ref _forwardCompactedSimpleNearFieldDirectSourcePipeline);
            DestroyOptionalPipeline(
                ref _forwardCompactedSimpleFullInputNearFieldDirectSourcePipeline);
            DestroyOptionalPipeline(
                ref _forwardReceiverCacheNearFieldDirectSourcePipeline);
            DestroyOptionalPipeline(
                ref _forwardCompactedReceiverCacheNearFieldDirectSourcePipeline);
            DestroyOptionalPipeline(
                ref _forwardSimpleReceiverCacheNearFieldDirectSourcePipeline);
            DestroyOptionalPipeline(
                ref _forwardSimpleFullInputReceiverCacheNearFieldDirectSourcePipeline);
            DestroyOptionalPipeline(
                ref _forwardCompactedSimpleReceiverCacheNearFieldDirectSourcePipeline);
            DestroyOptionalPipeline(
                ref _forwardCompactedSimpleFullInputReceiverCacheNearFieldDirectSourcePipeline);
        }

        private void DestroyGiCausticReceiverPipelines()
        {
            DestroyOptionalPipeline(ref _forwardGiCausticReceiverPipeline);
            DestroyOptionalPipeline(
                ref _forwardCompactedGiCausticReceiverPipeline);
            DestroyOptionalPipeline(
                ref _forwardSimpleGiCausticReceiverPipeline);
            DestroyOptionalPipeline(
                ref _forwardSimpleFullInputGiCausticReceiverPipeline);
            DestroyOptionalPipeline(
                ref _forwardCompactedSimpleGiCausticReceiverPipeline);
            DestroyOptionalPipeline(
                ref _forwardCompactedSimpleFullInputGiCausticReceiverPipeline);
            DestroyOptionalPipeline(
                ref _forwardReceiverCacheGiCausticReceiverPipeline);
            DestroyOptionalPipeline(
                ref _forwardCompactedReceiverCacheGiCausticReceiverPipeline);
            DestroyOptionalPipeline(
                ref _forwardSimpleReceiverCacheGiCausticReceiverPipeline);
            DestroyOptionalPipeline(
                ref _forwardSimpleFullInputReceiverCacheGiCausticReceiverPipeline);
            DestroyOptionalPipeline(
                ref _forwardCompactedSimpleReceiverCacheGiCausticReceiverPipeline);
            DestroyOptionalPipeline(
                ref _forwardCompactedSimpleFullInputReceiverCacheGiCausticReceiverPipeline);
        }

        private void DestroyCombinedAdvancedGiPipelines()
        {
            CombinedAdvancedGiAttachmentEnabled = false;
            DestroyOptionalPipeline(ref _forwardCombinedAdvancedGiPipeline);
            DestroyOptionalPipeline(
                ref _forwardCompactedCombinedAdvancedGiPipeline);
            DestroyOptionalPipeline(
                ref _forwardSimpleCombinedAdvancedGiPipeline);
            DestroyOptionalPipeline(
                ref _forwardSimpleFullInputCombinedAdvancedGiPipeline);
            DestroyOptionalPipeline(
                ref _forwardCompactedSimpleCombinedAdvancedGiPipeline);
            DestroyOptionalPipeline(
                ref _forwardCompactedSimpleFullInputCombinedAdvancedGiPipeline);
            DestroyOptionalPipeline(
                ref _forwardReceiverCacheCombinedAdvancedGiPipeline);
            DestroyOptionalPipeline(
                ref _forwardCompactedReceiverCacheCombinedAdvancedGiPipeline);
            DestroyOptionalPipeline(
                ref _forwardSimpleReceiverCacheCombinedAdvancedGiPipeline);
            DestroyOptionalPipeline(
                ref _forwardSimpleFullInputReceiverCacheCombinedAdvancedGiPipeline);
            DestroyOptionalPipeline(
                ref _forwardCompactedSimpleReceiverCacheCombinedAdvancedGiPipeline);
            DestroyOptionalPipeline(
                ref _forwardCompactedSimpleFullInputReceiverCacheCombinedAdvancedGiPipeline);
        }

        private void DestroyHybridReflectionPipelines()
        {
            HybridReflectionAttachmentEnabled = false;
            for (int receiver = 0;
                 receiver < HybridReflectionLaneCount;
                 receiver++)
            {
                for (int combination = 0; combination < 4; combination++)
                {
                    for (int family = 0; family < 6; family++)
                    {
                        VkPipeline pipeline = _hybridReflectionPipelines[
                            receiver,
                            combination,
                            family];
                        if (pipeline.Handle == 0)
                            continue;
                        _context.Api.DestroyPipeline(
                            _context.Device,
                            pipeline,
                            null);
                        _hybridReflectionPipelines[
                            receiver,
                            combination,
                            family] = default;
                    }
                }
            }
        }

        private void DestroyAlphaMaskReceiverFeedbackPipelines()
        {
            DestroyOptionalPipeline(
                ref _forwardAlphaMaskReceiverFeedbackPipeline);
            DestroyOptionalPipeline(
                ref _forwardCompactedAlphaMaskReceiverFeedbackPipeline);
            DestroyOptionalPipeline(
                ref _forwardSimpleAlphaMaskReceiverFeedbackPipeline);
            DestroyOptionalPipeline(
                ref _forwardSimpleFullInputAlphaMaskReceiverFeedbackPipeline);
            DestroyOptionalPipeline(
                ref _forwardCompactedSimpleAlphaMaskReceiverFeedbackPipeline);
            DestroyOptionalPipeline(
                ref _forwardCompactedSimpleFullInputAlphaMaskReceiverFeedbackPipeline);
        }

        private void DestroyTransparentReceiverFeedbackPipelines()
        {
            DestroyOptionalPipeline(ref _transparentReceiverFeedbackPipeline);
            DestroyOptionalPipeline(ref _thinGlassReceiverFeedbackPipeline);
            DestroyOptionalPipeline(ref _weightedOitReceiverFeedbackPipeline);
            DestroyOptionalPipeline(
                ref _rayTransparentReceiverFeedbackPipeline);
            DestroyOptionalPipeline(
                ref _rayWeightedOitReceiverFeedbackPipeline);
        }

        private void DestroyTransparentPartitionPipelines()
        {
            for (int index = 0;
                 index < _transparentPartitionPipelineCache.Length;
                 index++)
            {
                DestroyOptionalPipeline(
                    ref _transparentPartitionPipelineCache[index]);
                _transparentPartitionPipelineAttempted[index] = false;
                _transparentPartitionPipelineFailures[index] = null;
            }
        }

        private void MarkReceiverFeedbackPipelineStatesFailed()
        {
            _alphaMaskReceiverFeedbackPipelineState =
                DeferredPipelineState.Failed;
            _transparentReceiverFeedbackPipelineState =
                DeferredPipelineState.Failed;
            _thinGlassReceiverFeedbackPipelineState =
                DeferredPipelineState.Failed;
            _weightedOitReceiverFeedbackPipelineState =
                DeferredPipelineState.Failed;
            _rayTransparentReceiverFeedbackPipelineState =
                DeferredPipelineState.Failed;
            _rayWeightedOitReceiverFeedbackPipelineState =
                DeferredPipelineState.Failed;
        }

        private void ResetDeferredPipelineStates()
        {
            _rayTransparentPipelineState = DeferredPipelineState.NotAdmitted;
            _rayWeightedOitPipelineState = DeferredPipelineState.NotAdmitted;
            _alphaMaskReceiverFeedbackPipelineState =
                DeferredPipelineState.NotAdmitted;
            _transparentReceiverFeedbackPipelineState =
                DeferredPipelineState.NotAdmitted;
            _thinGlassReceiverFeedbackPipelineState =
                DeferredPipelineState.NotAdmitted;
            _weightedOitReceiverFeedbackPipelineState =
                DeferredPipelineState.NotAdmitted;
            _rayTransparentReceiverFeedbackPipelineState =
                DeferredPipelineState.NotAdmitted;
            _rayWeightedOitReceiverFeedbackPipelineState =
                DeferredPipelineState.NotAdmitted;
            _materialTransportProvenanceFormat = null;
        }

        private void DestroyOptionalPipeline(ref VkPipeline pipeline)
        {
            if (pipeline.Handle == 0)
                return;

            _context.Api.DestroyPipeline(_context.Device, pipeline, null);
            pipeline = default;
        }

        private void DestroyShaderModule(ShaderModule module)
        {
            if (module.Handle != 0)
                _context.Api.DestroyShaderModule(_context.Device, module, null);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;

            DestroyPipelines();

            if (_layout.Handle != 0)
                _context.Api.DestroyPipelineLayout(_context.Device, _layout, null);

            if (_rayTransparentLayout.Handle != 0)
            {
                _context.Api.DestroyPipelineLayout(
                    _context.Device,
                    _rayTransparentLayout,
                    null);
                _rayTransparentLayout = default;
            }

            if (_forwardReceiverCacheBufferSetLayout.Handle != 0)
            {
                _context.Api.DestroyDescriptorSetLayout(
                    _context.Device,
                    _forwardReceiverCacheBufferSetLayout,
                    null);
                _forwardReceiverCacheBufferSetLayout = default;
            }

            if (_sceneSubmissionComputeLayout.Handle != 0)
                _context.Api.DestroyPipelineLayout(_context.Device, _sceneSubmissionComputeLayout, null);

            if (_pipelineCacheService == null && _pipelineCache.Handle != 0)
                _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);

            if (_entryPointName != 0)
                SilkMarshal.Free(_entryPointName);

            System.Diagnostics.Debug.WriteLine("Mesh pipelines disposed.");
        }
    }
}
