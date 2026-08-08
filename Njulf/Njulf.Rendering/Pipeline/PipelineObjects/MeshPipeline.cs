using System;
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
    public sealed unsafe class MeshPipeline : IDisposable
    {
        private const string EntryPoint = "main";

        private readonly VulkanContext _context;
        private readonly BindlessHeap _bindlessHeap;
        private readonly nint _entryPointName;

        private VkPipeline _depthPipeline;
        private VkPipeline _maskedDepthPipeline;
        private VkPipeline _shadowDepthPipeline;
        private VkPipeline _shadowAlphaDepthPipeline;
        private VkPipeline _forwardPipeline;
        private VkPipeline _forwardCompactedPipeline;
        private VkPipeline _forwardSimplePipeline;
        private VkPipeline _forwardSimpleFullInputPipeline;
        private VkPipeline _forwardCompactedSimplePipeline;
        private VkPipeline _forwardCompactedSimpleFullInputPipeline;
        private VkPipeline _forwardReceiverCachePipeline;
        private VkPipeline _forwardCompactedReceiverCachePipeline;
        private VkPipeline _forwardSimpleReceiverCachePipeline;
        private VkPipeline _forwardSimpleFullInputReceiverCachePipeline;
        private VkPipeline _forwardCompactedSimpleReceiverCachePipeline;
        private VkPipeline _forwardCompactedSimpleFullInputReceiverCachePipeline;
        private VkPipeline _forwardGiDisabledPipeline;
        private VkPipeline _forwardCompactedGiDisabledPipeline;
        private VkPipeline _forwardSimpleGiDisabledPipeline;
        private VkPipeline _forwardSimpleFullInputGiDisabledPipeline;
        private VkPipeline _forwardCompactedSimpleGiDisabledPipeline;
        private VkPipeline _forwardCompactedSimpleFullInputGiDisabledPipeline;
        private VkPipeline _transparentForwardPipeline;
        private VkPipeline _weightedOitTransparentPipeline;
        private VkPipeline _motionVectorPipeline;
        private VkPipeline _sceneOpaqueCompactionPipeline;
        private VkPipeline _forwardVisibilityCompactionPipeline;
        private PipelineLayout _layout;
        private PipelineLayout _sceneSubmissionComputeLayout;
        private DescriptorSetLayout _forwardReceiverCacheBufferSetLayout;
        private PipelineCache _pipelineCache;
        private bool _disposed;

        public MeshPipeline(
            VulkanContext context,
            BindlessHeap bindlessHeap,
            Format colorFormat,
            Format depthFormat,
            RenderSettings settings)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bindlessHeap = bindlessHeap ?? throw new ArgumentNullException(nameof(bindlessHeap));
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
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
            CreateSceneSubmissionComputeLayout();
            CreatePipelines(colorFormat, depthFormat);
            CreateComputePipelines();
        }

        public VkPipeline DepthPipeline => _depthPipeline;
        public VkPipeline MaskedDepthPipeline => _maskedDepthPipeline;
        public VkPipeline ShadowDepthPipeline => _shadowDepthPipeline;
        public VkPipeline ShadowAlphaDepthPipeline => _shadowAlphaDepthPipeline;
        public VkPipeline ForwardPipeline => _forwardPipeline;
        public VkPipeline ForwardFullMaterialPipeline => _forwardPipeline;
        public VkPipeline ForwardCompactedPipeline => _forwardCompactedPipeline;
        public VkPipeline ForwardSimplePipeline => _forwardSimplePipeline;
        public VkPipeline ForwardSimpleGlobalIblPipeline => _forwardSimplePipeline;
        public VkPipeline ForwardSimpleFullInputGlobalIblPipeline => _forwardSimpleFullInputPipeline;
        public VkPipeline ForwardCompactedSimpleGlobalIblPipeline => _forwardCompactedSimplePipeline;
        public VkPipeline ForwardCompactedSimpleFullInputGlobalIblPipeline => _forwardCompactedSimpleFullInputPipeline;
        public VkPipeline TransparentForwardPipeline => _transparentForwardPipeline;
        public VkPipeline WeightedOitTransparentPipeline => _weightedOitTransparentPipeline;
        public VkPipeline MotionVectorPipeline => _motionVectorPipeline;
        public VkPipeline SceneOpaqueCompactionPipeline => _sceneOpaqueCompactionPipeline;
        public VkPipeline ForwardVisibilityCompactionPipeline => _forwardVisibilityCompactionPipeline;
        public VkPipeline Pipeline => _forwardPipeline;
        public PipelineLayout Layout => _layout;
        public PipelineLayout SceneSubmissionComputeLayout => _sceneSubmissionComputeLayout;
        internal DescriptorSetLayout ForwardReceiverCacheBufferSetLayout =>
            _forwardReceiverCacheBufferSetLayout;
        public RenderSettings Settings { get; }
        public bool GpuMeshletCountersEnabled { get; private set; }
        public bool MaterialTransportProvenanceAttachmentEnabled { get; private set; }

        public VkPipeline ResolveOpaqueSpecializedPipeline(
            VkPipeline exactPipeline,
            bool receiverCacheRequired,
            bool globalIlluminationDisabled)
        {
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
            // Pipeline creation is atomic at renderer construction/recreation.
            // The zero-handle guard keeps this resolver fail-safe if a future
            // optional cache backend deliberately omits its native variant.
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
            GpuMeshletCountersEnabled = Settings.Diagnostics.GpuMeshletCountersEnabled;
            string depthTaskShaderName = GpuMeshletCountersEnabled
                ? "depth_diagnostics.task.spv"
                : "depth.task.spv";
            string forwardTaskShaderName = GpuMeshletCountersEnabled
                ? "forward_diagnostics.task.spv"
                : "forward.task.spv";
            bool materialTransportProvenanceEnabled =
                Settings.GlobalIllumination.DebugView ==
                GlobalIlluminationDebugView.MaterialTransportHitProvenance;
            MaterialTransportProvenanceAttachmentEnabled =
                materialTransportProvenanceEnabled;
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

#if !DEBUG && !NJULF_DETAILED_INVESTIGATION
            if (!materialTransportProvenanceEnabled)
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

            _transparentForwardPipeline = CreateGraphicsPipeline(
                forwardTaskShaderName,
                "forward.mesh.spv",
                "forward.frag.spv",
                colorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: false,
                blendEnable: true,
                cullMode: CullModeFlags.None,
                depthBiasEnable: false);
            _context.SetDebugName(_transparentForwardPipeline.Handle, ObjectType.Pipeline, "Transparent Forward Plus Mesh Pipeline");

            _weightedOitTransparentPipeline = CreateWeightedOitGraphicsPipeline(
                forwardTaskShaderName,
                "forward.mesh.spv",
                "forward_weighted_oit.frag.spv",
                RenderTargetManager.WeightedOitAccumulationFormat,
                RenderTargetManager.WeightedOitRevealageFormat,
                depthFormat);
            _context.SetDebugName(_weightedOitTransparentPipeline.Handle, ObjectType.Pipeline, "Weighted OIT Transparent Mesh Pipeline");

            _motionVectorPipeline = CreateGraphicsPipeline(
                "motion_vector.task.spv",
                "motion_vector.mesh.spv",
                "motion_vector.frag.spv",
                Njulf.Rendering.Resources.RenderTargetManager.MotionVectorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: false,
                blendEnable: false,
                cullMode: CullModeFlags.BackBit,
                depthBiasEnable: false);
            _context.SetDebugName(_motionVectorPipeline.Handle, ObjectType.Pipeline, "Motion Vector Mesh Pipeline");

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
            out VkPipeline compactedSimpleFullInputPipeline)
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
                depthBiasEnable: false);
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
                depthBiasEnable: false);
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
                depthBiasEnable: false);
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
                depthBiasEnable: false);
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
                depthBiasEnable: false);
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
                depthBiasEnable: false);

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
            Format? materialTransportProvenanceFormat = null)
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

                return CreateGraphicsPipeline(
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
                materialTransportProvenanceFormat);
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
            string taskShaderName,
            string meshShaderName,
            string fragmentShaderName,
            Format accumulationFormat,
            Format revealageFormat,
            Format depthFormat)
        {
            ShaderModule taskModule = new ShaderModule();
            ShaderModule meshModule = new ShaderModule();
            ShaderModule fragmentModule = new ShaderModule();

            try
            {
                taskModule = ShaderModuleLoader.Load(_context, taskShaderName);
                _context.SetDebugName(taskModule.Handle, ObjectType.ShaderModule, taskShaderName);
                meshModule = ShaderModuleLoader.Load(_context, meshShaderName);
                _context.SetDebugName(meshModule.Handle, ObjectType.ShaderModule, meshShaderName);
                fragmentModule = ShaderModuleLoader.Load(_context, fragmentShaderName);
                _context.SetDebugName(fragmentModule.Handle, ObjectType.ShaderModule, fragmentShaderName);

                return CreateWeightedOitGraphicsPipeline(
                    taskModule,
                    meshModule,
                    fragmentModule,
                    accumulationFormat,
                    revealageFormat,
                    depthFormat);
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
            Format? materialTransportProvenanceFormat = null)
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
                SrcColorBlendFactor = blendEnable ? BlendFactor.SrcAlpha : BlendFactor.One,
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
            uint colorAttachmentCount =
                ForwardDynamicRenderingContract.ResolveColorAttachmentCount(
                    hasColorAttachment,
                    materialTransportProvenanceFormat.HasValue);
            var colorBlendAttachments = stackalloc PipelineColorBlendAttachmentState[2];
            colorBlendAttachments[0] = colorBlendAttachment;
            colorBlendAttachments[1] = colorBlendAttachment;

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

            var renderingColorFormats = stackalloc Format[2];
            renderingColorFormats[0] = colorFormat;
            renderingColorFormats[1] =
                secondaryColorFormat ??
                materialTransportProvenanceFormat ??
                colorFormat;
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
                Layout = _layout,
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
            Format depthFormat)
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
                Layout = _layout,
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

        private void DestroyPipelines()
        {
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

            if (_shadowAlphaDepthPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _shadowAlphaDepthPipeline, null);
                _shadowAlphaDepthPipeline = default;
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

            if (_weightedOitTransparentPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _weightedOitTransparentPipeline, null);
                _weightedOitTransparentPipeline = default;
            }

            if (_motionVectorPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _motionVectorPipeline, null);
                _motionVectorPipeline = default;
            }

            if (_sceneOpaqueCompactionPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _sceneOpaqueCompactionPipeline, null);
                _sceneOpaqueCompactionPipeline = default;
            }

            if (_forwardVisibilityCompactionPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _forwardVisibilityCompactionPipeline, null);
                _forwardVisibilityCompactionPipeline = default;
            }
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

            if (_pipelineCache.Handle != 0)
                _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);

            if (_entryPointName != 0)
                SilkMarshal.Free(_entryPointName);

            System.Diagnostics.Debug.WriteLine("Mesh pipelines disposed.");
        }
    }
}
