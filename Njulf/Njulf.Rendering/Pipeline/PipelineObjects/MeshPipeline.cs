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
        private VkPipeline _transparentForwardPipeline;
        private VkPipeline _weightedOitTransparentPipeline;
        private VkPipeline _motionVectorPipeline;
        private VkPipeline _sceneSurfacePipeline;
        private VkPipeline _sceneSurfaceSimplePipeline;
        private VkPipeline _sceneSurfaceCompactedPipeline;
        private VkPipeline _sceneOpaqueCompactionPipeline;
        private PipelineLayout _layout;
        private PipelineLayout _sceneSubmissionComputeLayout;
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
                Marshal.SizeOf<GPUSceneOpaqueCompactionPushConstants>()));
            CreatePipelineCache();
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
        public VkPipeline TransparentForwardPipeline => _transparentForwardPipeline;
        public VkPipeline WeightedOitTransparentPipeline => _weightedOitTransparentPipeline;
        public VkPipeline MotionVectorPipeline => _motionVectorPipeline;
        public VkPipeline SceneSurfacePipeline => _sceneSurfacePipeline;
        public VkPipeline SceneSurfaceSimplePipeline => _sceneSurfaceSimplePipeline;
        public VkPipeline SceneSurfaceCompactedPipeline => _sceneSurfaceCompactedPipeline;
        public VkPipeline SceneOpaqueCompactionPipeline => _sceneOpaqueCompactionPipeline;
        public VkPipeline Pipeline => _forwardPipeline;
        public PipelineLayout Layout => _layout;
        public PipelineLayout SceneSubmissionComputeLayout => _sceneSubmissionComputeLayout;
        public RenderSettings Settings { get; }
        public bool GpuMeshletCountersEnabled { get; private set; }

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

        private void CreatePipelineLayout()
        {
            var setLayouts = stackalloc DescriptorSetLayout[2];
            setLayouts[0] = _bindlessHeap.StorageBufferSetLayout;
            setLayouts[1] = _bindlessHeap.TextureSamplerSetLayout;

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
                SetLayoutCount = 2,
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
            var setLayouts = stackalloc DescriptorSetLayout[1];
            setLayouts[0] = _bindlessHeap.StorageBufferSetLayout;

            var pushConstantRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.ComputeBit,
                Offset = 0,
                Size = (uint)Marshal.SizeOf<GPUSceneOpaqueCompactionPushConstants>()
            };

            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
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
            string forwardCompactedTaskShaderName = GpuMeshletCountersEnabled
                ? "forward_compacted_diagnostics.task.spv"
                : "forward_compacted.task.spv";

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
                "forward_opaque.frag.spv",
                colorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: false,
                blendEnable: false,
                cullMode: CullModeFlags.None,
                depthBiasEnable: false,
                secondaryColorFormat: colorFormat);
            _context.SetDebugName(_forwardPipeline.Handle, ObjectType.Pipeline, "Opaque Forward Plus Mesh Pipeline");

            _forwardCompactedPipeline = CreateGraphicsPipeline(
                forwardCompactedTaskShaderName,
                "forward.mesh.spv",
                "forward_opaque.frag.spv",
                colorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: false,
                blendEnable: false,
                cullMode: CullModeFlags.None,
                depthBiasEnable: false,
                secondaryColorFormat: colorFormat);
            _context.SetDebugName(_forwardCompactedPipeline.Handle, ObjectType.Pipeline, "Compacted Opaque Forward Plus Mesh Pipeline");

            _forwardSimplePipeline = CreateGraphicsPipeline(
                forwardTaskShaderName,
                "forward_simple.mesh.spv",
                "forward_opaque_simple.frag.spv",
                colorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: false,
                blendEnable: false,
                cullMode: CullModeFlags.None,
                depthBiasEnable: false,
                secondaryColorFormat: colorFormat);
            _context.SetDebugName(_forwardSimplePipeline.Handle, ObjectType.Pipeline, "Simple Opaque Forward Plus Mesh Pipeline");

            _forwardSimpleFullInputPipeline = CreateGraphicsPipeline(
                forwardTaskShaderName,
                "forward.mesh.spv",
                "forward_opaque_simple_full_input.frag.spv",
                colorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: false,
                blendEnable: false,
                cullMode: CullModeFlags.None,
                depthBiasEnable: false,
                secondaryColorFormat: colorFormat);
            _context.SetDebugName(_forwardSimpleFullInputPipeline.Handle, ObjectType.Pipeline, "Simple Full-Input Opaque Forward Plus Mesh Pipeline");

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

            _sceneSurfacePipeline = CreateSceneSurfaceGraphicsPipeline(
                forwardTaskShaderName,
                "forward.mesh.spv",
                "scene_surface.frag.spv",
                depthFormat);
            _context.SetDebugName(_sceneSurfacePipeline.Handle, ObjectType.Pipeline, "Scene Surface Mesh Pipeline");

            _sceneSurfaceSimplePipeline = CreateSceneSurfaceGraphicsPipeline(
                forwardTaskShaderName,
                "forward_simple.mesh.spv",
                "scene_surface_simple.frag.spv",
                depthFormat);
            _context.SetDebugName(_sceneSurfaceSimplePipeline.Handle, ObjectType.Pipeline, "Simple Scene Surface Mesh Pipeline");

            _sceneSurfaceCompactedPipeline = CreateSceneSurfaceGraphicsPipeline(
                forwardCompactedTaskShaderName,
                "forward.mesh.spv",
                "scene_surface.frag.spv",
                depthFormat);
            _context.SetDebugName(_sceneSurfaceCompactedPipeline.Handle, ObjectType.Pipeline, "Compacted Scene Surface Mesh Pipeline");
        }

        private void CreateComputePipelines()
        {
            _sceneOpaqueCompactionPipeline = CreateComputePipeline("scene_opaque_compact.comp.spv", _sceneSubmissionComputeLayout);
            _context.SetDebugName(_sceneOpaqueCompactionPipeline.Handle, ObjectType.Pipeline, "Scene Opaque Compaction Compute Pipeline");
        }

        private VkPipeline CreateGraphicsPipeline(
            string taskShaderName,
            string meshShaderName,
            string? fragmentShaderName,
            Format colorFormat,
            Format depthFormat,
            bool hasColorAttachment,
            bool depthWriteEnable,
            bool blendEnable,
            CullModeFlags cullMode,
            bool depthBiasEnable,
            Format? secondaryColorFormat = null)
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
                secondaryColorFormat);
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

        private VkPipeline CreateSceneSurfaceGraphicsPipeline(
            string taskShaderName,
            string meshShaderName,
            string fragmentShaderName,
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

                return CreateSceneSurfaceGraphicsPipeline(taskModule, meshModule, fragmentModule, depthFormat);
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
            Format? secondaryColorFormat = null)
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[3];
            stages[0] = CreateShaderStageInfo(ShaderStageFlags.TaskBitExt, taskModule);
            stages[1] = CreateShaderStageInfo(ShaderStageFlags.MeshBitExt, meshModule);

            uint stageCount = 2;
            if (fragmentModule.Handle != 0)
            {
                stages[2] = CreateShaderStageInfo(ShaderStageFlags.FragmentBit, fragmentModule);
                stageCount = 3;
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
            uint colorAttachmentCount = hasColorAttachment
                ? secondaryColorFormat.HasValue ? 2u : 1u
                : 0u;
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
            renderingColorFormats[1] = secondaryColorFormat ?? colorFormat;
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
                StageCount = stageCount,
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

        private VkPipeline CreateSceneSurfaceGraphicsPipeline(
            ShaderModule taskModule,
            ShaderModule meshModule,
            ShaderModule fragmentModule,
            Format depthFormat)
        {
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
            for (int i = 0; i < 2; i++)
            {
                colorBlendAttachments[i] = new PipelineColorBlendAttachmentState
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
            }

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
            colorFormats[0] = RenderTargetManager.SceneNormalFormat;
            colorFormats[1] = RenderTargetManager.SceneMaterialFormat;
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
                throw new VulkanException("Failed to create scene surface mesh graphics pipeline", result);

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

            if (_sceneSurfacePipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _sceneSurfacePipeline, null);
                _sceneSurfacePipeline = default;
            }

            if (_sceneSurfaceSimplePipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _sceneSurfaceSimplePipeline, null);
                _sceneSurfaceSimplePipeline = default;
            }

            if (_sceneSurfaceCompactedPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _sceneSurfaceCompactedPipeline, null);
                _sceneSurfaceCompactedPipeline = default;
            }

            if (_sceneOpaqueCompactionPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _sceneOpaqueCompactionPipeline, null);
                _sceneOpaqueCompactionPipeline = default;
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
