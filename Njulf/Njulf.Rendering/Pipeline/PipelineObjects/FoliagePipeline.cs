using System;
using System.IO;
using System.Runtime.InteropServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using Njulf.Rendering.Utilities;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline.PipelineObjects
{
    public sealed unsafe class FoliagePipeline : IDisposable
    {
        private const string EntryPoint = "main";

        private readonly VulkanContext _context;
        private readonly BindlessHeap _bindlessHeap;
        private readonly bool _receiverFeedbackPipelinesEnabled;
        private readonly GiPipelineCacheService? _pipelineCacheService;
        private readonly nint _entryPointName;
        private PipelineLayout _computeLayout;
        private PipelineLayout _graphicsLayout;
        private PipelineCache _pipelineCache;
        private VkPipeline _cullPipeline;
        private VkPipeline _depthPipeline;
        private VkPipeline _forwardPipeline;
        private VkPipeline _forwardReceiverFeedbackPipeline;
        private VkPipeline _forwardNearFieldDirectSourcePipeline;
        private VkPipeline _forwardReceiverFeedbackNearFieldDirectSourcePipeline;
        private VkPipeline _forwardCombinedAdvancedGiPipeline;
        private VkPipeline _forwardReceiverFeedbackCombinedAdvancedGiPipeline;
        private VkPipeline _authoredDepthPipeline;
        private VkPipeline _authoredForwardPipeline;
        private VkPipeline _authoredForwardReceiverFeedbackPipeline;
        private VkPipeline _authoredForwardNearFieldDirectSourcePipeline;
        private VkPipeline _authoredForwardReceiverFeedbackNearFieldDirectSourcePipeline;
        private VkPipeline _authoredForwardCombinedAdvancedGiPipeline;
        private VkPipeline _authoredForwardReceiverFeedbackCombinedAdvancedGiPipeline;
        private readonly VkPipeline[,] _hybridReflectionPipelines =
            new VkPipeline[4, 2];
        private readonly ForwardHybridReflectionReceiverPipelineConfiguration
            _hybridReflectionConfiguration;
        private readonly ForwardNearFieldDirectSourcePipelineConfiguration
            _nearFieldDirectSourceConfiguration;
        private Format _colorFormat;
        private Format _motionVectorFormat;
        private Format _depthFormat;
        private VkPipeline _shadowPipeline;
        private VkPipeline _authoredShadowPipeline;
        private VkPipeline _authoredMotionVectorPipeline;
        private bool _pipelinesPrepared;
        private bool _disposed;

        public FoliagePipeline(
            VulkanContext context,
            BindlessHeap bindlessHeap,
            Format colorFormat,
            Format motionVectorFormat,
            Format depthFormat,
            RenderSettings settings,
            bool receiverFeedbackPipelinesEnabled = false,
            ForwardNearFieldDirectSourcePipelineConfiguration
                nearFieldDirectSourceConfiguration = default,
            ForwardGiCausticReceiverPipelineConfiguration
                giCausticReceiverConfiguration = default,
            ForwardHybridReflectionReceiverPipelineConfiguration
                hybridReflectionConfiguration = default)
            : this(
                context,
                bindlessHeap,
                colorFormat,
                motionVectorFormat,
                depthFormat,
                settings,
                receiverFeedbackPipelinesEnabled,
                nearFieldDirectSourceConfiguration,
                giCausticReceiverConfiguration,
                hybridReflectionConfiguration,
                pipelineCacheService: null,
                createPipelines: true)
        {
        }

        internal FoliagePipeline(
            VulkanContext context,
            BindlessHeap bindlessHeap,
            Format colorFormat,
            Format motionVectorFormat,
            Format depthFormat,
            RenderSettings settings,
            bool receiverFeedbackPipelinesEnabled,
            ForwardNearFieldDirectSourcePipelineConfiguration
                nearFieldDirectSourceConfiguration,
            ForwardGiCausticReceiverPipelineConfiguration
                giCausticReceiverConfiguration,
            ForwardHybridReflectionReceiverPipelineConfiguration
                hybridReflectionConfiguration,
            GiPipelineCacheService? pipelineCacheService,
            bool createPipelines)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bindlessHeap = bindlessHeap ?? throw new ArgumentNullException(nameof(bindlessHeap));
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _pipelineCacheService = pipelineCacheService;
            _receiverFeedbackPipelinesEnabled =
                receiverFeedbackPipelinesEnabled;
            _nearFieldDirectSourceConfiguration =
                nearFieldDirectSourceConfiguration;
            NearFieldDirectSourcePipelinesRequested =
                ForwardNearFieldDirectSourceContract
                    .TryValidatePipelineConfiguration(
                        nearFieldDirectSourceConfiguration,
                        out _);
            CombinedAdvancedGiPipelinesRequested =
                NearFieldDirectSourcePipelinesRequested &&
                nearFieldDirectSourceConfiguration.SourceProducerMode ==
                    SimpleDdgiNearFieldSourceProducerMode.ForwardMrt &&
                ForwardAdvancedGiCombinedContract
                    .TryValidatePipelineConfigurations(
                        giCausticReceiverConfiguration,
                        nearFieldDirectSourceConfiguration,
                        out _);
            _hybridReflectionConfiguration = hybridReflectionConfiguration;
            _entryPointName = SilkMarshal.StringToPtr(EntryPoint);
            _colorFormat = colorFormat;
            _motionVectorFormat = motionVectorFormat;
            _depthFormat = depthFormat;

            ValidatePushConstantRange((uint)Math.Max(
                Marshal.SizeOf<GPUFoliageCullPushConstants>(),
                Math.Max(
                    Marshal.SizeOf<GPUFoliageDrawPushConstants>(),
                    Marshal.SizeOf<GPUMotionVectorPushConstants>())));
            CreatePipelineCache();
            CreateLayouts();
            if (createPipelines)
                Prepare();
        }

        public PipelineLayout ComputeLayout => _computeLayout;
        public PipelineLayout GraphicsLayout => _graphicsLayout;
        public VkPipeline CullPipeline => _cullPipeline;
        public VkPipeline DepthPipeline => _depthPipeline;
        public VkPipeline ForwardPipeline => _forwardPipeline;
        public VkPipeline ForwardReceiverFeedbackPipeline =>
            _forwardReceiverFeedbackPipeline;
        public VkPipeline AuthoredDepthPipeline => _authoredDepthPipeline;
        public VkPipeline AuthoredForwardPipeline => _authoredForwardPipeline;
        public VkPipeline AuthoredForwardReceiverFeedbackPipeline =>
            _authoredForwardReceiverFeedbackPipeline;
        public bool NearFieldDirectSourcePipelinesRequested { get; }
        public bool CombinedAdvancedGiPipelinesRequested { get; }
        public bool NearFieldDirectSourcePipelinesAvailable =>
            _forwardNearFieldDirectSourcePipeline.Handle != 0 &&
            _authoredForwardNearFieldDirectSourcePipeline.Handle != 0 &&
            (_nearFieldDirectSourceConfiguration.SourceProducerMode ==
                 SimpleDdgiNearFieldSourceProducerMode.TraceResolutionRaster ||
             !_receiverFeedbackPipelinesEnabled ||
             _forwardReceiverFeedbackNearFieldDirectSourcePipeline.Handle != 0 &&
             _authoredForwardReceiverFeedbackNearFieldDirectSourcePipeline.Handle != 0);
        public bool CombinedAdvancedGiPipelinesAvailable =>
            _forwardCombinedAdvancedGiPipeline.Handle != 0 &&
            _authoredForwardCombinedAdvancedGiPipeline.Handle != 0 &&
            (!_receiverFeedbackPipelinesEnabled ||
             _forwardReceiverFeedbackCombinedAdvancedGiPipeline.Handle != 0 &&
             _authoredForwardReceiverFeedbackCombinedAdvancedGiPipeline.Handle != 0);
        public string NearFieldDirectSourcePipelineFailureReason { get; private set; } =
            "near-field-foliage-pipelines-not-requested";
        public bool ReceiverFeedbackPipelinesAvailable =>
            _forwardReceiverFeedbackPipeline.Handle != 0 &&
            _authoredForwardReceiverFeedbackPipeline.Handle != 0;
        public string ReceiverFeedbackPipelineFailureReason { get; private set; } =
            "receiver-feedback-pipelines-not-admitted-at-startup";
        public VkPipeline ShadowPipeline => _shadowPipeline;
        public VkPipeline AuthoredShadowPipeline => _authoredShadowPipeline;
        public VkPipeline AuthoredMotionVectorPipeline => _authoredMotionVectorPipeline;
        public RenderSettings Settings { get; }
        /// <summary>
        /// Uses a separate foliage shadow mesh shader with bounded caster
        /// attribution. The normal depth/forward foliage shaders remain free
        /// of those atomics.
        /// </summary>
        public bool GpuMeshletCountersEnabled { get; private set; }
        public bool MaterialTransportProvenanceAttachmentEnabled { get; private set; }
        public bool HybridReflectionPipelinesAvailable { get; private set; }
        public string HybridReflectionPipelineFailureReason { get; private set; } =
            "hybrid-reflection-foliage-pipelines-not-requested";

        internal bool IsPrepared => _pipelinesPrepared;

        internal void Prepare()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pipelinesPrepared)
                return;

            try
            {
                CreatePipelines(
                    _colorFormat,
                    _motionVectorFormat,
                    _depthFormat);
                _pipelinesPrepared = true;
            }
            catch
            {
                DestroyPipelines();
                throw;
            }
        }

        public bool TryResolveHybridReflectionPipeline(
            bool authored,
            bool nearFieldDirectSource,
            bool giCausticReceiver,
            out VkPipeline pipeline)
        {
            pipeline = default;
            if (!HybridReflectionPipelinesAvailable)
                return false;

            int combination = (giCausticReceiver ? 1 : 0) |
                (nearFieldDirectSource ? 2 : 0);
            int family = authored ? 1 : 0;
            if (_hybridReflectionPipelines[combination, family].Handle == 0 &&
                !TryCreateHybridReflectionPipeline(combination, family))
            {
                return false;
            }
            pipeline = _hybridReflectionPipelines[combination, family];
            return pipeline.Handle != 0;
        }

        public bool TryPrepareHybridReflectionPipelines(
            bool nearFieldDirectSource,
            bool giCausticReceiver)
        {
            if (!HybridReflectionPipelinesAvailable)
                return false;

            int combination = (giCausticReceiver ? 1 : 0) |
                (nearFieldDirectSource ? 2 : 0);
            for (int family = 0; family < 2; family++)
            {
                if (_hybridReflectionPipelines[combination, family].Handle == 0 &&
                    !TryCreateHybridReflectionPipeline(combination, family))
                {
                    return false;
                }
            }
            return true;
        }

        public bool TryResolveForwardPipeline(
            bool authored,
            bool receiverFeedback,
            bool nearFieldDirectSource,
            bool combinedAdvancedGi,
            out VkPipeline pipeline)
        {
            if (!nearFieldDirectSource)
            {
                pipeline = authored
                    ? receiverFeedback
                        ? _authoredForwardReceiverFeedbackPipeline
                        : _authoredForwardPipeline
                    : receiverFeedback
                        ? _forwardReceiverFeedbackPipeline
                        : _forwardPipeline;
                return pipeline.Handle != 0;
            }

            pipeline = authored
                ? combinedAdvancedGi
                    ? receiverFeedback
                        ? _authoredForwardReceiverFeedbackCombinedAdvancedGiPipeline
                        : _authoredForwardCombinedAdvancedGiPipeline
                    : receiverFeedback
                        ? _authoredForwardReceiverFeedbackNearFieldDirectSourcePipeline
                        : _authoredForwardNearFieldDirectSourcePipeline
                : combinedAdvancedGi
                    ? receiverFeedback
                        ? _forwardReceiverFeedbackCombinedAdvancedGiPipeline
                        : _forwardCombinedAdvancedGiPipeline
                    : receiverFeedback
                        ? _forwardReceiverFeedbackNearFieldDirectSourcePipeline
                        : _forwardNearFieldDirectSourcePipeline;
            return pipeline.Handle != 0;
        }

        public void Recreate(Format colorFormat, Format motionVectorFormat, Format depthFormat)
        {
            _colorFormat = colorFormat;
            _motionVectorFormat = motionVectorFormat;
            _depthFormat = depthFormat;
            if (!_pipelinesPrepared)
                return;

            DestroyPipelines();
            Prepare();
        }

        private void ValidatePushConstantRange(uint requiredSize)
        {
            var properties = new PhysicalDeviceProperties();
            _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice, &properties);
            if (requiredSize > properties.Limits.MaxPushConstantsSize)
            {
                throw new VulkanException(
                    $"GPU supports {properties.Limits.MaxPushConstantsSize} bytes of push constants, but foliage requires {requiredSize} bytes.");
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

            Result result = _context.Api.CreatePipelineCache(_context.Device, &cacheInfo, null, out _pipelineCache);
            if (result != Result.Success)
                throw new VulkanException("Failed to create foliage pipeline cache", result);
            _context.SetDebugName(_pipelineCache.Handle, ObjectType.PipelineCache, "Foliage Pipeline Cache");
        }

        private void CreateLayouts()
        {
            var setLayouts = stackalloc DescriptorSetLayout[2];
            setLayouts[0] = _bindlessHeap.StorageBufferSetLayout;
            setLayouts[1] = _bindlessHeap.TextureSamplerSetLayout;

            var computePushRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.ComputeBit,
                Offset = 0,
                Size = (uint)Marshal.SizeOf<GPUFoliageCullPushConstants>()
            };
            var computeLayoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 2,
                PSetLayouts = setLayouts,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &computePushRange
            };
            Result result = _context.Api.CreatePipelineLayout(_context.Device, &computeLayoutInfo, null, out _computeLayout);
            if (result != Result.Success)
                throw new VulkanException("Failed to create foliage compute pipeline layout", result);
            _context.SetDebugName(_computeLayout.Handle, ObjectType.PipelineLayout, "Foliage Compute Pipeline Layout");

            var graphicsPushRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.TaskBitExt | ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit,
                Offset = 0,
                Size = (uint)Math.Max(
                    Marshal.SizeOf<GPUFoliageDrawPushConstants>(),
                    Marshal.SizeOf<GPUMotionVectorPushConstants>())
            };
            var graphicsLayoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 2,
                PSetLayouts = setLayouts,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &graphicsPushRange
            };
            result = _context.Api.CreatePipelineLayout(_context.Device, &graphicsLayoutInfo, null, out _graphicsLayout);
            if (result != Result.Success)
                throw new VulkanException("Failed to create foliage graphics pipeline layout", result);
            _context.SetDebugName(_graphicsLayout.Handle, ObjectType.PipelineLayout, "Foliage Graphics Pipeline Layout");
        }

        private void CreatePipelines(Format colorFormat, Format motionVectorFormat, Format depthFormat)
        {
            _colorFormat = colorFormat;
            _depthFormat = depthFormat;
            GpuMeshletCountersEnabled = Settings.Diagnostics.GpuMeshletCountersEnabled;
            string foliageGrassShadowMeshShader = GpuMeshletCountersEnabled
                ? "foliage_grass_diagnostics.mesh.spv"
                : "foliage_grass.mesh.spv";
            string foliageMeshShadowMeshShader = GpuMeshletCountersEnabled
                ? "foliage_mesh_diagnostics.mesh.spv"
                : "foliage_mesh.mesh.spv";
            bool materialTransportProvenanceEnabled =
                Settings.GlobalIllumination.DebugView ==
                GlobalIlluminationDebugView.MaterialTransportHitProvenance;
            MaterialTransportProvenanceAttachmentEnabled =
                materialTransportProvenanceEnabled;
            HybridReflectionPipelinesAvailable =
                !materialTransportProvenanceEnabled &&
                _hybridReflectionConfiguration.Enabled &&
                _hybridReflectionConfiguration.ShaderSemanticVersion ==
                    ForwardHybridReflectionReceiverContract.ShaderSemanticVersion;
            HybridReflectionPipelineFailureReason =
                HybridReflectionPipelinesAvailable
                    ? "valid; variants created on first use"
                    : materialTransportProvenanceEnabled
                        ? "hybrid-reflection-foliage-provenance-conflict"
                        : "hybrid-reflection-foliage-configuration-invalid";
            string provenanceSuffix =
                materialTransportProvenanceEnabled ? "_provenance" : string.Empty;
            string foliageForwardFragmentShaderName =
                $"foliage_forward_ddgi{provenanceSuffix}.frag.spv";
            Format? materialTransportProvenanceFormat =
                materialTransportProvenanceEnabled
                    ? RenderTargetManager.MaterialTransportProvenanceFormat
                    : null;

            _cullPipeline = CreateComputePipeline("foliage_cull.comp.spv");
            _context.SetDebugName(_cullPipeline.Handle, ObjectType.Pipeline, "Foliage Cull Compute Pipeline");

            _depthPipeline = CreateGraphicsPipeline(
                "foliage_grass.task.spv",
                "foliage_grass.mesh.spv",
                "foliage_depth.frag.spv",
                colorFormat,
                depthFormat,
                hasColorAttachment: false,
                depthWriteEnable: true);
            _context.SetDebugName(_depthPipeline.Handle, ObjectType.Pipeline, "Foliage Grass Depth Pipeline");

            _shadowPipeline = CreateGraphicsPipeline(
                "foliage_grass.task.spv",
                foliageGrassShadowMeshShader,
                "foliage_depth.frag.spv",
                colorFormat,
                depthFormat,
                hasColorAttachment: false,
                depthWriteEnable: true,
                depthBiasEnable: true);
            _context.SetDebugName(_shadowPipeline.Handle, ObjectType.Pipeline, "Foliage Grass Shadow Pipeline");

            _forwardPipeline = CreateGraphicsPipeline(
                "foliage_grass.task.spv",
                "foliage_grass.mesh.spv",
                foliageForwardFragmentShaderName,
                colorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: false,
                secondaryColorFormat: null,
                materialTransportProvenanceFormat: materialTransportProvenanceFormat);
            _context.SetDebugName(_forwardPipeline.Handle, ObjectType.Pipeline, "Foliage Grass Forward Pipeline");

            _authoredDepthPipeline = CreateGraphicsPipeline(
                "foliage_mesh.task.spv",
                "foliage_mesh.mesh.spv",
                "foliage_depth.frag.spv",
                colorFormat,
                depthFormat,
                hasColorAttachment: false,
                depthWriteEnable: true);
            _context.SetDebugName(_authoredDepthPipeline.Handle, ObjectType.Pipeline, "Foliage Authored Meshlet Depth Pipeline");

            _authoredShadowPipeline = CreateGraphicsPipeline(
                "foliage_mesh.task.spv",
                foliageMeshShadowMeshShader,
                "foliage_depth.frag.spv",
                colorFormat,
                depthFormat,
                hasColorAttachment: false,
                depthWriteEnable: true,
                depthBiasEnable: true);
            _context.SetDebugName(_authoredShadowPipeline.Handle, ObjectType.Pipeline, "Foliage Authored Meshlet Shadow Pipeline");

            _authoredForwardPipeline = CreateGraphicsPipeline(
                "foliage_mesh.task.spv",
                "foliage_mesh.mesh.spv",
                foliageForwardFragmentShaderName,
                colorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: false,
                secondaryColorFormat: null,
                materialTransportProvenanceFormat: materialTransportProvenanceFormat);
            _context.SetDebugName(_authoredForwardPipeline.Handle, ObjectType.Pipeline, "Foliage Authored Meshlet Forward Pipeline");

            if (_receiverFeedbackPipelinesEnabled)
            {
                try
                {
                    string receiverFeedbackFragmentShader =
                        $"foliage_forward_ddgi_b1{provenanceSuffix}.frag.spv";
                    _forwardReceiverFeedbackPipeline = CreateGraphicsPipeline(
                        "foliage_grass.task.spv",
                        "foliage_grass_b1.mesh.spv",
                        receiverFeedbackFragmentShader,
                        colorFormat,
                        depthFormat,
                        hasColorAttachment: true,
                        depthWriteEnable: false,
                        secondaryColorFormat: null,
                        materialTransportProvenanceFormat:
                            materialTransportProvenanceFormat);
                    _context.SetDebugName(
                        _forwardReceiverFeedbackPipeline.Handle,
                        ObjectType.Pipeline,
                        "Foliage Grass B1 Exact Receiver Feedback Pipeline");

                    _authoredForwardReceiverFeedbackPipeline =
                        CreateGraphicsPipeline(
                            "foliage_mesh.task.spv",
                            "foliage_mesh_b1.mesh.spv",
                            receiverFeedbackFragmentShader,
                            colorFormat,
                            depthFormat,
                            hasColorAttachment: true,
                            depthWriteEnable: false,
                            secondaryColorFormat: null,
                            materialTransportProvenanceFormat:
                                materialTransportProvenanceFormat);
                    _context.SetDebugName(
                        _authoredForwardReceiverFeedbackPipeline.Handle,
                        ObjectType.Pipeline,
                        "Foliage Authored B1 Exact Receiver Feedback Pipeline");
                    ReceiverFeedbackPipelineFailureReason =
                        "receiver-feedback-pipelines-ready";
                }
                catch (Exception exception) when (
                    exception is VulkanException or IOException or
                    ArgumentException or InvalidOperationException)
                {
                    DestroyPipeline(ref _forwardReceiverFeedbackPipeline);
                    DestroyPipeline(
                        ref _authoredForwardReceiverFeedbackPipeline);
                    ReceiverFeedbackPipelineFailureReason =
                        "receiver-feedback-foliage-pipeline-creation-failed:" +
                        exception.GetType().Name + ":" + exception.Message;
                    System.Diagnostics.Debug.WriteLine(
                        "B1 foliage pipelines unavailable; ordinary foliage " +
                        "rendering retained. " +
                        ReceiverFeedbackPipelineFailureReason);
                }
            }

            CreateNearFieldDirectSourcePipelines(colorFormat, depthFormat);

            _authoredMotionVectorPipeline = CreateGraphicsPipeline(
                "foliage_motion.task.spv",
                "foliage_motion.mesh.spv",
                "foliage_motion.frag.spv",
                motionVectorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: false);
            _context.SetDebugName(_authoredMotionVectorPipeline.Handle, ObjectType.Pipeline, "Foliage Authored Meshlet Motion Vector Pipeline");
        }

        private void CreateNearFieldDirectSourcePipelines(
            Format colorFormat,
            Format depthFormat)
        {
            if (!NearFieldDirectSourcePipelinesRequested)
                return;

            try
            {
                if (_nearFieldDirectSourceConfiguration.SourceProducerMode ==
                    SimpleDdgiNearFieldSourceProducerMode.TraceResolutionRaster)
                {
                    _forwardNearFieldDirectSourcePipeline = CreateGraphicsPipeline(
                        "foliage_grass.task.spv",
                        "foliage_grass.mesh.spv",
                        ForwardNearFieldDirectSourceContract
                            .TraceResolutionFoliageFragmentShader,
                        ForwardNearFieldDirectSourceContract.RequiredAttachmentFormat,
                        depthFormat,
                        hasColorAttachment: true,
                        depthWriteEnable: true,
                        secondaryColorFormat:
                            ForwardNearFieldDirectSourceContract
                                .ReceiverPayloadFormat);
                    _authoredForwardNearFieldDirectSourcePipeline =
                        CreateGraphicsPipeline(
                            "foliage_mesh.task.spv",
                            "foliage_mesh.mesh.spv",
                            ForwardNearFieldDirectSourceContract
                                .TraceResolutionFoliageFragmentShader,
                            ForwardNearFieldDirectSourceContract
                                .RequiredAttachmentFormat,
                            depthFormat,
                            hasColorAttachment: true,
                            depthWriteEnable: true,
                            secondaryColorFormat:
                                ForwardNearFieldDirectSourceContract
                                    .ReceiverPayloadFormat);
                    NearFieldDirectSourcePipelineFailureReason =
                        "near-field-trace-foliage-pipelines-ready";
                    return;
                }

                _forwardNearFieldDirectSourcePipeline = CreateGraphicsPipeline(
                    "foliage_grass.task.spv",
                    "foliage_grass.mesh.spv",
                    "foliage_forward_ddgi_near_field_direct_source.frag.spv",
                    colorFormat,
                    depthFormat,
                    hasColorAttachment: true,
                    depthWriteEnable: false,
                    secondaryColorFormat:
                        ForwardNearFieldDirectSourceContract.RequiredAttachmentFormat,
                    tertiaryColorFormat:
                        ForwardNearFieldDirectSourceContract.ReceiverPayloadFormat);
                _authoredForwardNearFieldDirectSourcePipeline =
                    CreateGraphicsPipeline(
                        "foliage_mesh.task.spv",
                        "foliage_mesh.mesh.spv",
                        "foliage_forward_ddgi_near_field_direct_source.frag.spv",
                        colorFormat,
                        depthFormat,
                        hasColorAttachment: true,
                        depthWriteEnable: false,
                        secondaryColorFormat:
                            ForwardNearFieldDirectSourceContract.RequiredAttachmentFormat,
                        tertiaryColorFormat:
                            ForwardNearFieldDirectSourceContract.ReceiverPayloadFormat);

                if (_receiverFeedbackPipelinesEnabled)
                {
                    _forwardReceiverFeedbackNearFieldDirectSourcePipeline =
                        CreateGraphicsPipeline(
                            "foliage_grass.task.spv",
                            "foliage_grass_b1.mesh.spv",
                            "foliage_forward_ddgi_b1_near_field_direct_source.frag.spv",
                            colorFormat,
                            depthFormat,
                            hasColorAttachment: true,
                            depthWriteEnable: false,
                            secondaryColorFormat:
                                ForwardNearFieldDirectSourceContract.RequiredAttachmentFormat,
                            tertiaryColorFormat:
                                ForwardNearFieldDirectSourceContract.ReceiverPayloadFormat);
                    _authoredForwardReceiverFeedbackNearFieldDirectSourcePipeline =
                        CreateGraphicsPipeline(
                            "foliage_mesh.task.spv",
                            "foliage_mesh_b1.mesh.spv",
                            "foliage_forward_ddgi_b1_near_field_direct_source.frag.spv",
                            colorFormat,
                            depthFormat,
                            hasColorAttachment: true,
                            depthWriteEnable: false,
                            secondaryColorFormat:
                                ForwardNearFieldDirectSourceContract.RequiredAttachmentFormat,
                            tertiaryColorFormat:
                                ForwardNearFieldDirectSourceContract.ReceiverPayloadFormat);
                }
                NearFieldDirectSourcePipelineFailureReason =
                    "near-field-foliage-pipelines-ready";
            }
            catch (Exception exception) when (
                exception is VulkanException or IOException or
                ArgumentException or InvalidOperationException)
            {
                DestroyNearFieldDirectSourcePipelines(includeCombined: false);
                NearFieldDirectSourcePipelineFailureReason =
                    "near-field-foliage-pipeline-creation-failed:" +
                    exception.GetType().Name + ":" + exception.Message;
                System.Diagnostics.Debug.WriteLine(
                    NearFieldDirectSourcePipelineFailureReason);
                return;
            }

            if (!CombinedAdvancedGiPipelinesRequested)
                return;

            try
            {
                _forwardCombinedAdvancedGiPipeline = CreateGraphicsPipeline(
                    "foliage_grass.task.spv",
                    "foliage_grass.mesh.spv",
                    "foliage_forward_ddgi_c4_c5.frag.spv",
                    colorFormat,
                    depthFormat,
                    hasColorAttachment: true,
                    depthWriteEnable: false,
                    secondaryColorFormat:
                        ForwardGiCausticReceiverContract.ReceiverPayloadFormat,
                    tertiaryColorFormat:
                        ForwardNearFieldDirectSourceContract.RequiredAttachmentFormat,
                    quaternaryColorFormat:
                        ForwardNearFieldDirectSourceContract.ReceiverPayloadFormat);
                _authoredForwardCombinedAdvancedGiPipeline =
                    CreateGraphicsPipeline(
                        "foliage_mesh.task.spv",
                        "foliage_mesh.mesh.spv",
                        "foliage_forward_ddgi_c4_c5.frag.spv",
                        colorFormat,
                        depthFormat,
                        hasColorAttachment: true,
                        depthWriteEnable: false,
                        secondaryColorFormat:
                            ForwardGiCausticReceiverContract.ReceiverPayloadFormat,
                        tertiaryColorFormat:
                            ForwardNearFieldDirectSourceContract.RequiredAttachmentFormat,
                        quaternaryColorFormat:
                            ForwardNearFieldDirectSourceContract.ReceiverPayloadFormat);
                if (_receiverFeedbackPipelinesEnabled)
                {
                    _forwardReceiverFeedbackCombinedAdvancedGiPipeline =
                        CreateGraphicsPipeline(
                            "foliage_grass.task.spv",
                            "foliage_grass_b1.mesh.spv",
                            "foliage_forward_ddgi_b1_c4_c5.frag.spv",
                            colorFormat,
                            depthFormat,
                            hasColorAttachment: true,
                            depthWriteEnable: false,
                            secondaryColorFormat:
                                ForwardGiCausticReceiverContract.ReceiverPayloadFormat,
                            tertiaryColorFormat:
                                ForwardNearFieldDirectSourceContract.RequiredAttachmentFormat,
                            quaternaryColorFormat:
                                ForwardNearFieldDirectSourceContract.ReceiverPayloadFormat);
                    _authoredForwardReceiverFeedbackCombinedAdvancedGiPipeline =
                        CreateGraphicsPipeline(
                            "foliage_mesh.task.spv",
                            "foliage_mesh_b1.mesh.spv",
                            "foliage_forward_ddgi_b1_c4_c5.frag.spv",
                            colorFormat,
                            depthFormat,
                            hasColorAttachment: true,
                            depthWriteEnable: false,
                            secondaryColorFormat:
                                ForwardGiCausticReceiverContract.ReceiverPayloadFormat,
                            tertiaryColorFormat:
                                ForwardNearFieldDirectSourceContract.RequiredAttachmentFormat,
                            quaternaryColorFormat:
                                ForwardNearFieldDirectSourceContract.ReceiverPayloadFormat);
                }
            }
            catch (Exception exception) when (
                exception is VulkanException or IOException or
                ArgumentException or InvalidOperationException)
            {
                DestroyCombinedAdvancedGiPipelines();
                NearFieldDirectSourcePipelineFailureReason =
                    "combined-advanced-gi-foliage-pipeline-creation-failed:" +
                    exception.GetType().Name + ":" + exception.Message;
                System.Diagnostics.Debug.WriteLine(
                    NearFieldDirectSourcePipelineFailureReason);
            }
        }

        private bool TryCreateHybridReflectionPipeline(
            int combination,
            int family)
        {
            bool giCaustic = (combination & 1) != 0;
            bool nearField = (combination & 2) != 0;
            string producer = (giCaustic, nearField) switch
            {
                (true, true) => "c4_c5_",
                (true, false) => "c4_",
                (false, true) => "c5_",
                _ => string.Empty
            };
            string fragmentShader =
                $"foliage_forward_ddgi_{producer}hybrid_reflection.frag.spv";
            bool authored = family == 1;
            string taskShader = authored
                ? "foliage_mesh.task.spv"
                : "foliage_grass.task.spv";
            string meshShader = authored
                ? "foliage_mesh.mesh.spv"
                : "foliage_grass.mesh.spv";

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
                VkPipeline created = CreateGraphicsPipeline(
                    taskShader,
                    meshShader,
                    fragmentShader,
                    _colorFormat,
                    _depthFormat,
                    hasColorAttachment: true,
                    depthWriteEnable: false,
                    secondaryColorFormat: secondary,
                    tertiaryColorFormat: tertiary,
                    quaternaryColorFormat: quaternary,
                    quinaryColorFormat: quinary);
                _hybridReflectionPipelines[combination, family] = created;
                _context.SetDebugName(created.Handle, ObjectType.Pipeline,
                    $"Hybrid Reflection Foliage Pipeline C{combination} F{family}");
                HybridReflectionPipelineFailureReason = "valid";
                return true;
            }
            catch (Exception exception)
            {
                HybridReflectionPipelineFailureReason =
                    "hybrid-reflection-foliage-pipeline-creation-failed:" +
                    exception.GetType().Name + ":" + exception.Message;
                return false;
            }
        }

        private VkPipeline CreateComputePipeline(string shaderName)
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
                    Layout = _computeLayout,
                    BasePipelineHandle = default,
                    BasePipelineIndex = -1
                };

                long pipelineStart =
                    _pipelineCacheService?.BeginPipelineCreation() ?? 0L;
                Result result;
                VkPipeline pipeline;
                try
                {
                    result = _context.Api.CreateComputePipelines(
                        _context.Device,
                        _pipelineCache,
                        1,
                        &pipelineInfo,
                        null,
                        out pipeline);
                }
                finally
                {
                    _pipelineCacheService?.EndPipelineCreation(
                        $"Foliage:{shaderName}",
                        pipelineStart);
                }
                if (result != Result.Success)
                    throw new VulkanException("Failed to create foliage compute pipeline", result);
                return pipeline;
            }
            finally
            {
                DestroyShaderModule(shaderModule);
            }
        }

        private VkPipeline CreateGraphicsPipeline(
            string taskShaderName,
            string meshShaderName,
            string fragmentShaderName,
            Format colorFormat,
            Format depthFormat,
            bool hasColorAttachment,
            bool depthWriteEnable,
            bool depthBiasEnable = false,
            Format? secondaryColorFormat = null,
            Format? materialTransportProvenanceFormat = null,
            Format? tertiaryColorFormat = null,
            Format? quaternaryColorFormat = null,
            Format? quinaryColorFormat = null)
        {
            ShaderModule taskModule = default;
            ShaderModule meshModule = default;
            ShaderModule fragmentModule = default;
            try
            {
                taskModule = ShaderModuleLoader.Load(_context, taskShaderName);
                meshModule = ShaderModuleLoader.Load(_context, meshShaderName);
                fragmentModule = ShaderModuleLoader.Load(_context, fragmentShaderName);
                _context.SetDebugName(taskModule.Handle, ObjectType.ShaderModule, taskShaderName);
                _context.SetDebugName(meshModule.Handle, ObjectType.ShaderModule, meshShaderName);
                _context.SetDebugName(fragmentModule.Handle, ObjectType.ShaderModule, fragmentShaderName);

                var stages = stackalloc PipelineShaderStageCreateInfo[3];
                stages[0] = CreateShaderStageInfo(ShaderStageFlags.TaskBitExt, taskModule);
                stages[1] = CreateShaderStageInfo(ShaderStageFlags.MeshBitExt, meshModule);
                stages[2] = CreateShaderStageInfo(ShaderStageFlags.FragmentBit, fragmentModule);

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
                    BlendEnable = false,
                    SrcColorBlendFactor = BlendFactor.One,
                    DstColorBlendFactor = BlendFactor.Zero,
                    ColorBlendOp = BlendOp.Add,
                    SrcAlphaBlendFactor = BlendFactor.One,
                    DstAlphaBlendFactor = BlendFactor.Zero,
                    AlphaBlendOp = BlendOp.Add,
                    ColorWriteMask = ColorComponentFlags.RBit |
                                     ColorComponentFlags.GBit |
                                     ColorComponentFlags.BBit |
                                     ColorComponentFlags.ABit
                };
                if (materialTransportProvenanceFormat.HasValue &&
                    (secondaryColorFormat.HasValue ||
                     tertiaryColorFormat.HasValue ||
                     quaternaryColorFormat.HasValue ||
                     quinaryColorFormat.HasValue))
                {
                    throw new InvalidOperationException(
                        "Foliage provenance and advanced-GI MRT formats are mutually exclusive.");
                }
                if (tertiaryColorFormat.HasValue &&
                    !secondaryColorFormat.HasValue ||
                    quaternaryColorFormat.HasValue &&
                    !tertiaryColorFormat.HasValue ||
                    quinaryColorFormat.HasValue &&
                    !quaternaryColorFormat.HasValue)
                {
                    throw new InvalidOperationException(
                        "Foliage MRT formats must be contiguous.");
                }
                uint colorAttachmentCount = hasColorAttachment
                    ? 1u +
                      (secondaryColorFormat.HasValue ||
                       materialTransportProvenanceFormat.HasValue ? 1u : 0u) +
                       (tertiaryColorFormat.HasValue ? 1u : 0u) +
                       (quaternaryColorFormat.HasValue ? 1u : 0u) +
                       (quinaryColorFormat.HasValue ? 1u : 0u)
                    : 0u;
                var colorBlendAttachments =
                    stackalloc PipelineColorBlendAttachmentState[5];
                for (int attachment = 0; attachment < 5; attachment++)
                    colorBlendAttachments[attachment] = colorBlendAttachment;
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
                    DynamicStateCount = depthBiasEnable ? 3u : 2u,
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
                    StageCount = 3,
                    PStages = stages,
                    PVertexInputState = &vertexInputInfo,
                    PInputAssemblyState = &inputAssemblyInfo,
                    PViewportState = &viewportInfo,
                    PRasterizationState = &rasterInfo,
                    PMultisampleState = &multisampleInfo,
                    PDepthStencilState = &depthStencilInfo,
                    PColorBlendState = &colorBlendInfo,
                    PDynamicState = &dynamicInfo,
                    Layout = _graphicsLayout,
                    RenderPass = default,
                    Subpass = 0,
                    BasePipelineHandle = default,
                    BasePipelineIndex = -1
                };

                long pipelineStart =
                    _pipelineCacheService?.BeginPipelineCreation() ?? 0L;
                Result result;
                VkPipeline pipeline;
                try
                {
                    result = _context.Api.CreateGraphicsPipelines(
                        _context.Device,
                        _pipelineCache,
                        1,
                        &pipelineInfo,
                        null,
                        out pipeline);
                }
                finally
                {
                    _pipelineCacheService?.EndPipelineCreation(
                        $"Foliage:{taskShaderName}+{meshShaderName}+" +
                        fragmentShaderName,
                        pipelineStart);
                }
                if (result != Result.Success)
                {
                    throw new VulkanException(
                        "Failed to create foliage graphics pipeline",
                        result);
                }
                return pipeline;
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "Failed to create foliage graphics pipeline " +
                    $"(task='{taskShaderName}', mesh='{meshShaderName}', " +
                    $"fragment='{fragmentShaderName}'): " +
                    $"{exception.GetType().Name}: {exception.Message}",
                    exception);
            }
            finally
            {
                DestroyShaderModule(fragmentModule);
                DestroyShaderModule(meshModule);
                DestroyShaderModule(taskModule);
            }
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

        private void DestroyPipelines()
        {
            DestroyPipeline(ref _cullPipeline);
            DestroyPipeline(ref _depthPipeline);
            DestroyPipeline(ref _forwardPipeline);
            DestroyPipeline(ref _forwardReceiverFeedbackPipeline);
            DestroyNearFieldDirectSourcePipelines(includeCombined: true);
            DestroyHybridReflectionPipelines();
            DestroyPipeline(ref _authoredDepthPipeline);
            DestroyPipeline(ref _authoredForwardPipeline);
            DestroyPipeline(ref _authoredForwardReceiverFeedbackPipeline);
            DestroyPipeline(ref _shadowPipeline);
            DestroyPipeline(ref _authoredShadowPipeline);
            DestroyPipeline(ref _authoredMotionVectorPipeline);
            _pipelinesPrepared = false;
        }

        private void DestroyNearFieldDirectSourcePipelines(bool includeCombined)
        {
            DestroyPipeline(ref _forwardNearFieldDirectSourcePipeline);
            DestroyPipeline(
                ref _forwardReceiverFeedbackNearFieldDirectSourcePipeline);
            DestroyPipeline(ref _authoredForwardNearFieldDirectSourcePipeline);
            DestroyPipeline(
                ref _authoredForwardReceiverFeedbackNearFieldDirectSourcePipeline);
            if (includeCombined)
                DestroyCombinedAdvancedGiPipelines();
        }

        private void DestroyCombinedAdvancedGiPipelines()
        {
            DestroyPipeline(ref _forwardCombinedAdvancedGiPipeline);
            DestroyPipeline(
                ref _forwardReceiverFeedbackCombinedAdvancedGiPipeline);
            DestroyPipeline(ref _authoredForwardCombinedAdvancedGiPipeline);
            DestroyPipeline(
                ref _authoredForwardReceiverFeedbackCombinedAdvancedGiPipeline);
        }

        private void DestroyHybridReflectionPipelines()
        {
            HybridReflectionPipelinesAvailable = false;
            for (int combination = 0; combination < 4; combination++)
            {
                for (int family = 0; family < 2; family++)
                {
                    VkPipeline pipeline =
                        _hybridReflectionPipelines[combination, family];
                    if (pipeline.Handle == 0)
                        continue;
                    _context.Api.DestroyPipeline(_context.Device, pipeline, null);
                    _hybridReflectionPipelines[combination, family] = default;
                }
            }
        }

        private void DestroyPipeline(ref VkPipeline pipeline)
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
            if (_disposed)
                return;
            _disposed = true;

            DestroyPipelines();
            if (_computeLayout.Handle != 0)
                _context.Api.DestroyPipelineLayout(_context.Device, _computeLayout, null);
            if (_graphicsLayout.Handle != 0)
                _context.Api.DestroyPipelineLayout(_context.Device, _graphicsLayout, null);
            if (_pipelineCacheService == null && _pipelineCache.Handle != 0)
                _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);
            SilkMarshal.Free(_entryPointName);
        }
    }
}
