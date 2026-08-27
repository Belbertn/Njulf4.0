using System;
using System.IO;
using System.Runtime.InteropServices;
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

    public sealed unsafe class MeshPipeline : IDisposable
    {
        private const string EntryPoint = "main";

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
        private readonly Action<string, Action>? _runStartupStep;
        private readonly nint _entryPointName;
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
        private VkPipeline _forwardCombinedAdvancedGiPipeline;
        private VkPipeline _forwardCompactedCombinedAdvancedGiPipeline;
        private VkPipeline _forwardSimpleCombinedAdvancedGiPipeline;
        private VkPipeline _forwardSimpleFullInputCombinedAdvancedGiPipeline;
        private VkPipeline _forwardCompactedSimpleCombinedAdvancedGiPipeline;
        private VkPipeline _forwardCompactedSimpleFullInputCombinedAdvancedGiPipeline;
        // [receiver-cache 0..1, C4/C5 combination 0..3,
        //  base pipeline family 0..5].
        private readonly VkPipeline[,,] _hybridReflectionPipelines =
            new VkPipeline[2, 4, 6];
        private readonly VkPipeline[] _forwardOpaquePipelineCache =
            new VkPipeline[ForwardOpaquePipelineKey.CacheEntryCount];
        private readonly bool[] _forwardOpaquePipelineCacheValid =
            new bool[ForwardOpaquePipelineKey.CacheEntryCount];
        private int _forwardOpaquePipelineCacheEntryCount;
        private VkPipeline _forwardReceiverCachePipeline;
        private VkPipeline _forwardCompactedReceiverCachePipeline;
        private VkPipeline _forwardSimpleReceiverCachePipeline;
        private VkPipeline _forwardSimpleFullInputReceiverCachePipeline;
        private VkPipeline _forwardCompactedSimpleReceiverCachePipeline;
        private VkPipeline _forwardCompactedSimpleFullInputReceiverCachePipeline;
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
        public VkPipeline TransparentForwardPipeline => _transparentForwardPipeline;
        public VkPipeline ThinGlassForwardPipeline => _thinGlassForwardPipeline;
        public VkPipeline GeometryDecalOverlayPipeline =>
            _geometryDecalOverlayPipeline;
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
        public VkPipeline RayWeightedOitTransparentPipeline
        {
            get
            {
                TryEnsureRayWeightedOitTransparentPipeline();
                return _rayWeightedOitTransparentPipeline;
            }
        }
        public VkPipeline RayTransparentReceiverFeedbackPipeline =>
            _rayTransparentReceiverFeedbackPipeline;
        public VkPipeline RayWeightedOitReceiverFeedbackPipeline
        {
            get
            {
                TryEnsureRayWeightedOitReceiverFeedbackPipeline();
                return _rayWeightedOitReceiverFeedbackPipeline;
            }
        }
        internal bool RayTransparentPipelinesAdmitted =>
            _rayTransparentPipelineState is
                DeferredPipelineState.Deferred or DeferredPipelineState.Ready;
        public bool RayTransparentPipelinesAvailable =>
            _rayTransparentLayout.Handle != 0 &&
            _rayTransparentForwardPipeline.Handle != 0;
        public string RayTransparentPipelineFailureReason { get; private set; } =
            "ray-query transparent pipelines are unavailable";
        public VkPipeline TransparentReceiverFeedbackPipeline =>
            _transparentReceiverFeedbackPipeline;
        public VkPipeline ThinGlassReceiverFeedbackPipeline =>
            _thinGlassReceiverFeedbackPipeline;
        public VkPipeline WeightedOitReceiverFeedbackPipeline
        {
            get
            {
                TryEnsureWeightedOitReceiverFeedbackPipeline();
                return _weightedOitReceiverFeedbackPipeline;
            }
        }
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
            int receiver = receiverCacheRequired ? 1 : 0;
            if (_hybridReflectionPipelines[receiver, combination, family]
                    .Handle == 0 &&
                !TryCreateHybridReflectionPipeline(
                    combination,
                    family,
                    receiverCacheRequired))
            {
                return false;
            }

            hybridPipeline =
                _hybridReflectionPipelines[receiver, combination, family];
            return hybridPipeline.Handle != 0;
        }

        public bool TryPrepareHybridReflectionPipelines(
            bool nearFieldDirectSourceEnabled,
            bool giCausticReceiverEnabled)
        {
            if (!HybridReflectionAttachmentEnabled)
                return false;

            int combination = (giCausticReceiverEnabled ? 1 : 0) |
                (nearFieldDirectSourceEnabled ? 2 : 0);
            for (int family = 0; family < 6; family++)
            {
                if (_hybridReflectionPipelines[0, combination, family].Handle == 0 &&
                    !TryCreateHybridReflectionPipeline(
                        combination,
                        family,
                        receiverCacheRequired: false))
                {
                    return false;
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

        private VkPipeline ResolveBasePipeline(
            ForwardOpaquePipelineFamily family) =>
            family switch
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
            if (alphaMaskReceiverFeedbackRequired)
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

            if (RendererBuildConfiguration.FastPipelineStartup &&
                !TryEnsureNearFieldDirectSourcePipeline(
                    exactPipeline,
                    receiverCacheRequired))
            {
                return false;
            }

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
            out VkPipeline causticPipeline)
        {
            causticPipeline = default;
            if (!GiCausticReceiverAttachmentEnabled)
                return false;

            if (RendererBuildConfiguration.FastPipelineStartup &&
                !TryEnsureGiCausticReceiverPipeline(exactPipeline))
            {
                return false;
            }

            if (exactPipeline.Handle == _forwardPipeline.Handle)
                causticPipeline = _forwardGiCausticReceiverPipeline;
            else if (exactPipeline.Handle == _forwardCompactedPipeline.Handle)
                causticPipeline = _forwardCompactedGiCausticReceiverPipeline;
            else if (exactPipeline.Handle == _forwardSimplePipeline.Handle)
                causticPipeline = _forwardSimpleGiCausticReceiverPipeline;
            else if (exactPipeline.Handle == _forwardSimpleFullInputPipeline.Handle)
                causticPipeline = _forwardSimpleFullInputGiCausticReceiverPipeline;
            else if (exactPipeline.Handle == _forwardCompactedSimplePipeline.Handle)
                causticPipeline = _forwardCompactedSimpleGiCausticReceiverPipeline;
            else if (exactPipeline.Handle ==
                _forwardCompactedSimpleFullInputPipeline.Handle)
            {
                causticPipeline =
                    _forwardCompactedSimpleFullInputGiCausticReceiverPipeline;
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
            out VkPipeline combinedPipeline)
        {
            combinedPipeline = default;
            if (!CombinedAdvancedGiAttachmentEnabled ||
                !NearFieldDirectSourceAttachmentEnabled ||
                !GiCausticReceiverAttachmentEnabled)
            {
                return false;
            }

            if (RendererBuildConfiguration.FastPipelineStartup &&
                !TryEnsureCombinedAdvancedGiPipeline(exactPipeline))
            {
                return false;
            }

            if (exactPipeline.Handle == _forwardPipeline.Handle)
                combinedPipeline = _forwardCombinedAdvancedGiPipeline;
            else if (exactPipeline.Handle == _forwardCompactedPipeline.Handle)
                combinedPipeline = _forwardCompactedCombinedAdvancedGiPipeline;
            else if (exactPipeline.Handle == _forwardSimplePipeline.Handle)
                combinedPipeline = _forwardSimpleCombinedAdvancedGiPipeline;
            else if (exactPipeline.Handle == _forwardSimpleFullInputPipeline.Handle)
                combinedPipeline = _forwardSimpleFullInputCombinedAdvancedGiPipeline;
            else if (exactPipeline.Handle == _forwardCompactedSimplePipeline.Handle)
                combinedPipeline = _forwardCompactedSimpleCombinedAdvancedGiPipeline;
            else if (exactPipeline.Handle ==
                _forwardCompactedSimpleFullInputPipeline.Handle)
            {
                combinedPipeline =
                    _forwardCompactedSimpleFullInputCombinedAdvancedGiPipeline;
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
            string fullFragment = receiverCacheRequired
                ? ForwardNearFieldDirectSourceContract
                    .ReceiverCacheOpaqueFragmentShader
                : ForwardNearFieldDirectSourceContract.OpaqueFragmentShader;
            string simpleFragment = receiverCacheRequired
                ? ForwardNearFieldDirectSourceContract
                    .ReceiverCacheSimpleOpaqueFragmentShader
                : ForwardNearFieldDirectSourceContract.SimpleOpaqueFragmentShader;
            string simpleFullInputFragment = receiverCacheRequired
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
                        "forward_compacted.mesh.spv",
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
                        "forward_simple_compacted.mesh.spv",
                        simpleFragment,
                        "C5 receiver-cache compacted simple",
                        AdvancedGiPipelineKind.NearField) &&
                    TryEnsureAdvancedGiPipeline(
                        exactPipeline,
                        _forwardCompactedSimpleFullInputPipeline,
                        ref _forwardCompactedSimpleFullInputReceiverCacheNearFieldDirectSourcePipeline,
                        null,
                        "forward_compacted.mesh.spv",
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
                    "forward_compacted.mesh.spv",
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
                    "forward_simple_compacted.mesh.spv",
                    simpleFragment,
                    "C5 compacted simple",
                    AdvancedGiPipelineKind.NearField) &&
                TryEnsureAdvancedGiPipeline(
                    exactPipeline,
                    _forwardCompactedSimpleFullInputPipeline,
                    ref _forwardCompactedSimpleFullInputNearFieldDirectSourcePipeline,
                    null,
                    "forward_compacted.mesh.spv",
                    simpleFullInputFragment,
                    "C5 compacted simple full-input",
                    AdvancedGiPipelineKind.NearField);
        }

        private bool TryEnsureGiCausticReceiverPipeline(
            VkPipeline exactPipeline)
        {
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
                    "forward_compacted.mesh.spv",
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
                    "forward_simple_compacted.mesh.spv",
                    ForwardGiCausticReceiverContract.SimpleOpaqueFragmentShader,
                    "C4 compacted simple",
                    AdvancedGiPipelineKind.Caustic) &&
                TryEnsureAdvancedGiPipeline(
                    exactPipeline,
                    _forwardCompactedSimpleFullInputPipeline,
                    ref _forwardCompactedSimpleFullInputGiCausticReceiverPipeline,
                    null,
                    "forward_compacted.mesh.spv",
                    ForwardGiCausticReceiverContract
                        .SimpleFullInputOpaqueFragmentShader,
                    "C4 compacted simple full-input",
                    AdvancedGiPipelineKind.Caustic);
        }

        private bool TryEnsureCombinedAdvancedGiPipeline(
            VkPipeline exactPipeline)
        {
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
                    "forward_compacted.mesh.spv",
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
                    "forward_simple_compacted.mesh.spv",
                    ForwardAdvancedGiCombinedContract.SimpleOpaqueFragmentShader,
                    "combined C4/C5 compacted simple",
                    AdvancedGiPipelineKind.Combined) &&
                TryEnsureAdvancedGiPipeline(
                    exactPipeline,
                    _forwardCompactedSimpleFullInputPipeline,
                    ref _forwardCompactedSimpleFullInputCombinedAdvancedGiPipeline,
                    null,
                    "forward_compacted.mesh.spv",
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
                specializedPipeline = CreateGraphicsPipeline(
                    taskShaderName,
                    meshShaderName,
                    fragmentShaderName,
                    _colorFormat,
                    _depthFormat,
                    hasColorAttachment: true,
                    depthWriteEnable: false,
                    blendEnable: false,
                    cullMode: CullModeFlags.None,
                    depthBiasEnable: false,
                    secondaryColorFormat: kind == AdvancedGiPipelineKind.NearField
                        ? ForwardNearFieldDirectSourceContract
                            .RequiredAttachmentFormat
                        : ForwardGiCausticReceiverContract.ReceiverPayloadFormat,
                    tertiaryColorFormat: kind switch
                    {
                        AdvancedGiPipelineKind.NearField =>
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
            var binding = new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit
            };
            var info = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 1,
                PBindings = &binding
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

            _depthPipeline = CreateGraphicsPipeline(
                depthTaskShaderName,
                "depth.mesh.spv",
                "depth_sided.frag.spv",
                colorFormat,
                depthFormat,
                hasColorAttachment: false,
                depthWriteEnable: true,
                blendEnable: false,
                cullMode: CullModeFlags.None,
                depthBiasEnable: false);
            _context.SetDebugName(_depthPipeline.Handle, ObjectType.Pipeline, "Depth Prepass Mesh Pipeline");

            _maskedDepthPipeline = CreateGraphicsPipeline(
                depthTaskShaderName,
                "depth_alpha.mesh.spv",
                "depth_alpha.frag.spv",
                colorFormat,
                depthFormat,
                hasColorAttachment: false,
                depthWriteEnable: true,
                blendEnable: false,
                cullMode: CullModeFlags.None,
                depthBiasEnable: false);
            _context.SetDebugName(_maskedDepthPipeline.Handle, ObjectType.Pipeline, "Masked Depth Alpha-Test Mesh Pipeline");

            _compactedDepthPipeline = CreateGraphicsPipeline(
                taskShaderName: null,
                "depth_compacted.mesh.spv",
                "depth_sided.frag.spv",
                colorFormat,
                depthFormat,
                hasColorAttachment: false,
                depthWriteEnable: true,
                blendEnable: false,
                cullMode: CullModeFlags.None,
                depthBiasEnable: false);
            _context.SetDebugName(
                _compactedDepthPipeline.Handle,
                ObjectType.Pipeline,
                "Compacted Mesh-Only Depth Prepass Pipeline");

            _compactedMaskedDepthPipeline = CreateGraphicsPipeline(
                taskShaderName: null,
                "depth_alpha_compacted.mesh.spv",
                "depth_alpha.frag.spv",
                colorFormat,
                depthFormat,
                hasColorAttachment: false,
                depthWriteEnable: true,
                blendEnable: false,
                cullMode: CullModeFlags.None,
                depthBiasEnable: false);
            _context.SetDebugName(
                _compactedMaskedDepthPipeline.Handle,
                ObjectType.Pipeline,
                "Compacted Mesh-Only Masked Depth Prepass Pipeline");

            _shadowDepthPipeline = CreateGraphicsPipeline(
                "shadow_depth.task.spv",
                "shadow_depth.mesh.spv",
                fragmentShaderName: null,
                colorFormat,
                Format.D32Sfloat,
                hasColorAttachment: false,
                depthWriteEnable: true,
                blendEnable: false,
                cullMode: CullModeFlags.BackBit,
                depthBiasEnable: true);
            _context.SetDebugName(_shadowDepthPipeline.Handle, ObjectType.Pipeline, "Directional Shadow Mesh Pipeline");

            _shadowAlphaDepthPipeline = CreateGraphicsPipeline(
                "shadow_depth.task.spv",
                "shadow_depth_alpha.mesh.spv",
                "depth_alpha.frag.spv",
                colorFormat,
                Format.D32Sfloat,
                hasColorAttachment: false,
                depthWriteEnable: true,
                blendEnable: false,
                cullMode: CullModeFlags.None,
                depthBiasEnable: true);
            _context.SetDebugName(_shadowAlphaDepthPipeline.Handle, ObjectType.Pipeline, "Alpha-Test Shadow Mesh Pipeline");

            _compactedShadowAlphaDepthPipeline = CreateGraphicsPipeline(
                taskShaderName: null,
                "shadow_depth_alpha_compacted.mesh.spv",
                "depth_alpha.frag.spv",
                colorFormat,
                Format.D32Sfloat,
                hasColorAttachment: false,
                depthWriteEnable: true,
                blendEnable: false,
                cullMode: CullModeFlags.None,
                depthBiasEnable: true);
            _context.SetDebugName(
                _compactedShadowAlphaDepthPipeline.Handle,
                ObjectType.Pipeline,
                "Compacted Mesh-Only Alpha-Test Shadow Pipeline");

            _forwardPipeline = CreateGraphicsPipeline(
                forwardTaskShaderName,
                "forward.mesh.spv",
                forwardOpaqueFragmentShaderName,
                colorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: false,
                blendEnable: false,
                cullMode: CullModeFlags.None,
                depthBiasEnable: false,
                secondaryColorFormat: null,
                materialTransportProvenanceFormat: materialTransportProvenanceFormat);
            _context.SetDebugName(_forwardPipeline.Handle, ObjectType.Pipeline, "Opaque Forward Plus Mesh Pipeline");

            _forwardCompactedPipeline = CreateGraphicsPipeline(
                taskShaderName: null,
                "forward_compacted.mesh.spv",
                forwardOpaqueFragmentShaderName,
                colorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: false,
                blendEnable: false,
                cullMode: CullModeFlags.None,
                depthBiasEnable: false,
                secondaryColorFormat: null,
                materialTransportProvenanceFormat: materialTransportProvenanceFormat);
            _context.SetDebugName(_forwardCompactedPipeline.Handle, ObjectType.Pipeline, "Compacted Opaque Forward Plus Mesh Pipeline");

            _forwardSimplePipeline = CreateGraphicsPipeline(
                forwardTaskShaderName,
                "forward_simple.mesh.spv",
                forwardOpaqueSimpleFragmentShaderName,
                colorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: false,
                blendEnable: false,
                cullMode: CullModeFlags.None,
                depthBiasEnable: false,
                secondaryColorFormat: null,
                materialTransportProvenanceFormat: materialTransportProvenanceFormat);
            _context.SetDebugName(_forwardSimplePipeline.Handle, ObjectType.Pipeline, "Simple Opaque Forward Plus Mesh Pipeline");

            _forwardSimpleFullInputPipeline = CreateGraphicsPipeline(
                forwardTaskShaderName,
                "forward.mesh.spv",
                forwardOpaqueSimpleFullInputFragmentShaderName,
                colorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: false,
                blendEnable: false,
                cullMode: CullModeFlags.None,
                depthBiasEnable: false,
                secondaryColorFormat: null,
                materialTransportProvenanceFormat: materialTransportProvenanceFormat);
            _context.SetDebugName(_forwardSimpleFullInputPipeline.Handle, ObjectType.Pipeline, "Simple Full-Input Opaque Forward Plus Mesh Pipeline");

            _forwardCompactedSimplePipeline = CreateGraphicsPipeline(
                taskShaderName: null,
                "forward_simple_compacted.mesh.spv",
                forwardOpaqueSimpleFragmentShaderName,
                colorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: false,
                blendEnable: false,
                cullMode: CullModeFlags.None,
                depthBiasEnable: false,
                secondaryColorFormat: null,
                materialTransportProvenanceFormat: materialTransportProvenanceFormat);
            _context.SetDebugName(_forwardCompactedSimplePipeline.Handle, ObjectType.Pipeline, "Compacted Simple Opaque Forward Plus Mesh Pipeline");

            _forwardCompactedSimpleFullInputPipeline = CreateGraphicsPipeline(
                taskShaderName: null,
                "forward_compacted.mesh.spv",
                forwardOpaqueSimpleFullInputFragmentShaderName,
                colorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: false,
                blendEnable: false,
                cullMode: CullModeFlags.None,
                depthBiasEnable: false,
                secondaryColorFormat: null,
                materialTransportProvenanceFormat: materialTransportProvenanceFormat);
            _context.SetDebugName(_forwardCompactedSimpleFullInputPipeline.Handle, ObjectType.Pipeline, "Compacted Simple Full-Input Opaque Forward Plus Mesh Pipeline");

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
                GiCausticReceiverAttachmentEnabled)
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
                RendererBuildConfiguration.FastPipelineStartup
                    ? null
                    : forwardTaskShaderName;
            string transparentMeshShaderName =
                RendererBuildConfiguration.FastPipelineStartup
                    ? "forward_compacted.mesh.spv"
                    : "forward.mesh.spv";
            _transparentTaskShaderName = transparentTaskShaderName;
            _transparentMeshShaderName = transparentMeshShaderName;
            _transparentForwardPipeline = CreateGraphicsPipeline(
                transparentTaskShaderName,
                transparentMeshShaderName,
                "forward.frag.spv",
                colorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: false,
                blendEnable: true,
                cullMode: CullModeFlags.None,
                depthBiasEnable: false);
            _context.SetDebugName(_transparentForwardPipeline.Handle, ObjectType.Pipeline, "Transparent Forward Plus Mesh Pipeline");

            _thinGlassForwardPipeline = CreateGraphicsPipeline(
                transparentTaskShaderName,
                transparentMeshShaderName,
                "forward_transparent_thin_glass.frag.spv",
                colorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: false,
                blendEnable: true,
                cullMode: CullModeFlags.None,
                depthBiasEnable: false);
            _context.SetDebugName(
                _thinGlassForwardPipeline.Handle,
                ObjectType.Pipeline,
                "DDGI Directional Thin Glass Mesh Pipeline");

            _geometryDecalOverlayPipeline = CreateGraphicsPipeline(
                transparentTaskShaderName,
                transparentMeshShaderName,
                "geometry_decal.frag.spv",
                colorFormat,
                depthFormat,
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

            _motionVectorPipeline = CreateGraphicsPipeline(
                "motion_vector.task.spv",
                "motion_vector.mesh.spv",
                "motion_vector.frag.spv",
                Njulf.Rendering.Resources.RenderTargetManager.MotionVectorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: false,
                blendEnable: false,
                cullMode: CullModeFlags.None,
                depthBiasEnable: false);
            _context.SetDebugName(
                _motionVectorPipeline.Handle,
                ObjectType.Pipeline,
                "Solid Motion Vector Mesh Pipeline");

            _maskedMotionVectorPipeline = CreateGraphicsPipeline(
                "motion_vector.task.spv",
                "motion_vector_alpha.mesh.spv",
                "motion_vector_alpha.frag.spv",
                Njulf.Rendering.Resources.RenderTargetManager.MotionVectorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: false,
                blendEnable: false,
                cullMode: CullModeFlags.None,
                depthBiasEnable: false);
            _context.SetDebugName(
                _maskedMotionVectorPipeline.Handle,
                ObjectType.Pipeline,
                "Masked Motion Vector Mesh Pipeline");

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
            bool receiverCacheRequired)
        {
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
                    ? "forward_simple_compacted.mesh.spv"
                    : "forward_simple.mesh.spv"
                : compacted
                    ? "forward_compacted.mesh.spv"
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
                1 => ForwardHybridReflectionReceiverContract.ReceiverPayloadFormat,
                2 => ForwardNearFieldDirectSourceContract.ReceiverPayloadFormat,
                3 => ForwardNearFieldDirectSourceContract.RequiredAttachmentFormat,
                _ => null
            };
            Format? quaternary = combination switch
            {
                2 => ForwardHybridReflectionReceiverContract.ReceiverPayloadFormat,
                3 => ForwardNearFieldDirectSourceContract.ReceiverPayloadFormat,
                _ => null
            };
            Format? quinary = combination == 3
                ? ForwardHybridReflectionReceiverContract.ReceiverPayloadFormat
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
                    hybridReflectionReceiverEnabled: true);
                _context.SetDebugName(
                    pipeline.Handle,
                    ObjectType.Pipeline,
                    $"Hybrid Reflection Forward Pipeline R{(receiverCacheRequired ? 1 : 0)} C{combination} F{family}");
                _hybridReflectionPipelines[
                    receiverCacheRequired ? 1 : 0,
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

                if (_forwardCompactedGiCausticReceiverPipeline.Handle == 0 ||
                    _forwardCompactedSimpleGiCausticReceiverPipeline.Handle == 0 ||
                    _forwardCompactedSimpleFullInputGiCausticReceiverPipeline.Handle == 0 ||
                    (!RendererBuildConfiguration.FastPipelineStartup &&
                     (_forwardGiCausticReceiverPipeline.Handle == 0 ||
                      _forwardSimpleGiCausticReceiverPipeline.Handle == 0 ||
                      _forwardSimpleFullInputGiCausticReceiverPipeline.Handle == 0)))
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

                if (_forwardCompactedCombinedAdvancedGiPipeline.Handle == 0 ||
                    _forwardCompactedSimpleCombinedAdvancedGiPipeline.Handle == 0 ||
                    _forwardCompactedSimpleFullInputCombinedAdvancedGiPipeline.Handle == 0 ||
                    (!RendererBuildConfiguration.FastPipelineStartup &&
                     (_forwardCombinedAdvancedGiPipeline.Handle == 0 ||
                      _forwardSimpleCombinedAdvancedGiPipeline.Handle == 0 ||
                      _forwardSimpleFullInputCombinedAdvancedGiPipeline.Handle == 0)))
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
            Format? materialTransportProvenanceFormat = null)
        {
            fullPipeline = CreateGraphicsPipeline(
                forwardTaskShaderName,
                "forward.mesh.spv",
                fullFragmentShaderName,
                colorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: false,
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
                "forward_compacted.mesh.spv",
                fullFragmentShaderName,
                colorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: false,
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
                depthWriteEnable: false,
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
                depthWriteEnable: false,
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
                "forward_simple_compacted.mesh.spv",
                simpleFragmentShaderName,
                colorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: false,
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
                "forward_compacted.mesh.spv",
                simpleFullInputFragmentShaderName,
                colorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: false,
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
            _sceneOpaqueCompactionPipeline = CreateComputePipeline("scene_opaque_compact.comp.spv", _sceneSubmissionComputeLayout);
            _context.SetDebugName(_sceneOpaqueCompactionPipeline.Handle, ObjectType.Pipeline, "Scene Opaque Compaction Compute Pipeline");
            if (GpuMeshletCountersEnabled)
            {
                _sceneOpaqueCompactionDiagnosticsPipeline = CreateComputePipeline(
                    "scene_opaque_compact_diagnostics.comp.spv",
                    _sceneSubmissionComputeLayout);
                _context.SetDebugName(
                    _sceneOpaqueCompactionDiagnosticsPipeline.Handle,
                    ObjectType.Pipeline,
                    "Scene Opaque Compaction Exact Shadow Diagnostics Pipeline");
            }
            _forwardVisibilityCompactionPipeline = CreateComputePipeline("forward_visibility_compact.comp.spv", _sceneSubmissionComputeLayout);
            _context.SetDebugName(_forwardVisibilityCompactionPipeline.Handle, ObjectType.Pipeline, "Forward Visibility Compaction Compute Pipeline");
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
            Format? materialTransportProvenanceFormat = null,
            PipelineLayout pipelineLayout = default,
            bool hybridReflectionReceiverEnabled = false,
            bool destinationColorModulationBlend = false)
        {
            ShaderModule taskModule = new ShaderModule();
            ShaderModule meshModule = new ShaderModule();
            ShaderModule fragmentModule = new ShaderModule();

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
                    $"MeshPipeline.Graphics.{fragmentShaderName ?? "depth-only"}.{meshShaderName}.{taskShaderName ?? "no-task"}",
                    () => CreateGraphicsPipeline(
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
                        materialTransportProvenanceFormat:
                            materialTransportProvenanceFormat,
                        pipelineLayout: pipelineLayout,
                        hybridReflectionReceiverEnabled:
                            hybridReflectionReceiverEnabled,
                        destinationColorModulationBlend:
                            destinationColorModulationBlend));
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
                () => CreateComputePipelineCore(shaderName, layout));
        }

        private VkPipeline CreateComputePipelineCore(
            string shaderName,
            PipelineLayout layout)
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

                Result result = _context.Api.CreateComputePipelines(
                    _context.Device,
                    _pipelineCache,
                    1,
                    &pipelineInfo,
                    null,
                    out VkPipeline pipeline);

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
                    $"MeshPipeline.Graphics.{fragmentShaderName}.{meshShaderName}.{taskShaderName ?? "no-task"}",
                    () => CreateWeightedOitGraphicsPipeline(
                        taskModule,
                        meshModule,
                        fragmentModule,
                        accumulationFormat,
                        revealageFormat,
                        depthFormat,
                        pipelineLayout));
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
            Format? materialTransportProvenanceFormat = null,
            PipelineLayout pipelineLayout = default,
            bool hybridReflectionReceiverEnabled = false,
            bool destinationColorModulationBlend = false)
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[3];
            int stageCount = 0;
            if (taskModule.Handle != 0)
                stages[stageCount++] = CreateShaderStageInfo(ShaderStageFlags.TaskBitExt, taskModule);
            stages[stageCount++] = CreateShaderStageInfo(ShaderStageFlags.MeshBitExt, meshModule);
            if (fragmentModule.Handle != 0)
                stages[stageCount++] = CreateShaderStageInfo(ShaderStageFlags.FragmentBit, fragmentModule);

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
                    quaternaryColorFormat.HasValue || quinaryColorFormat.HasValue) &&
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

            uint colorAttachmentCount = hybridReflectionReceiverEnabled
                ? quinaryColorFormat.HasValue ? 5u
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
            var colorBlendAttachments = stackalloc PipelineColorBlendAttachmentState[5];
            for (int attachmentIndex = 0;
                 attachmentIndex <
                    5;
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

            var renderingColorFormats = stackalloc Format[5];
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
            var renderingInfo = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = colorAttachmentCount,
                PColorAttachmentFormats = colorAttachmentCount > 0 ? renderingColorFormats : null,
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

            Result result = _context.Api.CreateGraphicsPipelines(
                _context.Device,
                _pipelineCache,
                1,
                &pipelineInfo,
                null,
                out VkPipeline pipeline);

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
            PipelineLayout pipelineLayout = default)
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[3];
            int stageCount = 0;
            if (taskModule.Handle != 0)
                stages[stageCount++] = CreateShaderStageInfo(ShaderStageFlags.TaskBitExt, taskModule);
            stages[stageCount++] = CreateShaderStageInfo(ShaderStageFlags.MeshBitExt, meshModule);
            stages[stageCount++] = CreateShaderStageInfo(ShaderStageFlags.FragmentBit, fragmentModule);

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

            Result result = _context.Api.CreateGraphicsPipelines(
                _context.Device,
                _pipelineCache,
                1,
                &pipelineInfo,
                null,
                out VkPipeline pipeline);

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

        private VkPipeline CreateTrackedPipeline(
            string name,
            Func<VkPipeline> create)
        {
            VkPipeline pipeline = default;
            RunStartupStep(name, () =>
            {
                long started =
                    _pipelineCacheService?.BeginPipelineCreation() ?? 0L;
                try
                {
                    pipeline = create();
                }
                finally
                {
                    _pipelineCacheService?.EndPipelineCreation(name, started);
                }
            });
            return pipeline;
        }

        private void DestroyPipelines()
        {
            InvalidateForwardOpaquePipelineCache();
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
        }

        private void DestroyHybridReflectionPipelines()
        {
            HybridReflectionAttachmentEnabled = false;
            for (int receiver = 0; receiver < 2; receiver++)
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
