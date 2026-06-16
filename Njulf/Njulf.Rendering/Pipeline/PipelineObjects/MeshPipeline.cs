using System;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
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
        private VkPipeline _forwardDepthWritePipeline;
        private VkPipeline _forwardValidatePipeline;
        private VkPipeline _transparentForwardPipeline;
        private VkPipeline _weightedOitTransparentPipeline;
        private VkPipeline _motionVectorPipeline;
        private PipelineLayout _layout;
        private PipelineCache _pipelineCache;
        private bool _disposed;

        public MeshPipeline(
            VulkanContext context,
            BindlessHeap bindlessHeap,
            Format colorFormat,
            Format depthFormat)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bindlessHeap = bindlessHeap ?? throw new ArgumentNullException(nameof(bindlessHeap));
            _entryPointName = SilkMarshal.StringToPtr(EntryPoint);

            ValidatePushConstantRange((uint)Math.Max(
                Math.Max(Marshal.SizeOf<GPUDepthPushConstants>(), Marshal.SizeOf<GPUForwardPushConstants>()),
                Marshal.SizeOf<GPUMotionVectorPushConstants>()));
            CreatePipelineCache();
            CreatePipelineLayout();
            CreatePipelines(colorFormat, depthFormat);
        }

        public VkPipeline DepthPipeline => _depthPipeline;
        public VkPipeline MaskedDepthPipeline => _maskedDepthPipeline;
        public VkPipeline ShadowDepthPipeline => _shadowDepthPipeline;
        public VkPipeline ShadowAlphaDepthPipeline => _shadowAlphaDepthPipeline;
        public VkPipeline ForwardPipeline => _forwardPipeline;
        public VkPipeline ForwardDepthWritePipeline => _forwardDepthWritePipeline;
        public VkPipeline ForwardValidatePipeline => _forwardValidatePipeline;
        public VkPipeline TransparentForwardPipeline => _transparentForwardPipeline;
        public VkPipeline WeightedOitTransparentPipeline => _weightedOitTransparentPipeline;
        public bool WeightedOitSupported => _context.IndependentBlendSupported;
        public VkPipeline MotionVectorPipeline => _motionVectorPipeline;
        public VkPipeline Pipeline => _forwardPipeline;
        public PipelineLayout Layout => _layout;

        public void Recreate(Format colorFormat, Format depthFormat)
        {
            DestroyPipelines();
            CreatePipelines(colorFormat, depthFormat);
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

        private void CreatePipelines(Format colorFormat, Format depthFormat)
        {
            _depthPipeline = CreateGraphicsPipeline(
                "depth.task.spv",
                "depth.mesh.spv",
                fragmentShaderName: null,
                colorFormat,
                depthFormat,
                hasColorAttachment: false,
                depthWriteEnable: true,
                blendEnable: false,
                cullMode: CullModeFlags.BackBit,
                depthBiasEnable: false);
            _context.SetDebugName(_depthPipeline.Handle, ObjectType.Pipeline, "Depth Prepass Mesh Pipeline");

            _maskedDepthPipeline = CreateGraphicsPipeline(
                "depth.task.spv",
                "depth_alpha.mesh.spv",
                "depth_alpha.frag.spv",
                colorFormat,
                depthFormat,
                hasColorAttachment: false,
                depthWriteEnable: true,
                blendEnable: false,
                cullMode: CullModeFlags.BackBit,
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
                cullMode: CullModeFlags.BackBit,
                depthBiasEnable: true);
            _context.SetDebugName(_shadowAlphaDepthPipeline.Handle, ObjectType.Pipeline, "Alpha-Test Shadow Mesh Pipeline");

            _forwardPipeline = CreateGraphicsPipeline(
                "forward.task.spv",
                "forward.mesh.spv",
                "forward.frag.spv",
                colorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: false,
                blendEnable: false,
                cullMode: CullModeFlags.BackBit,
                depthBiasEnable: false);
            _context.SetDebugName(_forwardPipeline.Handle, ObjectType.Pipeline, "Opaque Forward Plus Mesh Pipeline");

            _forwardDepthWritePipeline = CreateGraphicsPipeline(
                "forward.task.spv",
                "forward.mesh.spv",
                "forward.frag.spv",
                colorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: true,
                blendEnable: false,
                cullMode: CullModeFlags.BackBit,
                depthBiasEnable: false);
            _context.SetDebugName(_forwardDepthWritePipeline.Handle, ObjectType.Pipeline, "Opaque Forward Plus Depth Write Mesh Pipeline");

            _forwardValidatePipeline = CreateGraphicsPipeline(
                "forward_validate.task.spv",
                "forward.mesh.spv",
                "forward.frag.spv",
                colorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: false,
                blendEnable: false,
                cullMode: CullModeFlags.BackBit,
                depthBiasEnable: false);
            _context.SetDebugName(_forwardValidatePipeline.Handle, ObjectType.Pipeline, "Opaque Forward Plus Validate Mesh Pipeline");

            _transparentForwardPipeline = CreateGraphicsPipeline(
                "forward.task.spv",
                "forward.mesh.spv",
                "forward.frag.spv",
                colorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: false,
                blendEnable: true,
                cullMode: CullModeFlags.None,
                depthBiasEnable: true);
            _context.SetDebugName(_transparentForwardPipeline.Handle, ObjectType.Pipeline, "Transparent Forward Plus Mesh Pipeline");

            if (_context.IndependentBlendSupported)
            {
                _weightedOitTransparentPipeline = CreateGraphicsPipeline(
                    "forward.task.spv",
                    "forward.mesh.spv",
                    "forward.frag.spv",
                    colorFormat,
                    depthFormat,
                    hasColorAttachment: true,
                    depthWriteEnable: false,
                    blendEnable: true,
                    cullMode: CullModeFlags.None,
                    depthBiasEnable: true,
                    weightedOit: true);
                _context.SetDebugName(_weightedOitTransparentPipeline.Handle, ObjectType.Pipeline, "Weighted OIT Transparent Mesh Pipeline");
            }
            else
            {
                _weightedOitTransparentPipeline = _transparentForwardPipeline;
            }

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
            bool weightedOit = false)
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
                    weightedOit);
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
            bool weightedOit = false)
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

            var colorBlendAttachments = stackalloc PipelineColorBlendAttachmentState[2];
            colorBlendAttachments[0] = weightedOit
                ? AdditiveBlendAttachment()
                : AlphaBlendAttachment(blendEnable);
            colorBlendAttachments[1] = RevealageBlendAttachment();

            var colorBlendInfo = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                LogicOpEnable = false,
                AttachmentCount = hasColorAttachment ? weightedOit ? 2u : 1u : 0u,
                PAttachments = hasColorAttachment ? colorBlendAttachments : null
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
            renderingColorFormats[0] = weightedOit
                ? Njulf.Rendering.Resources.RenderTargetManager.WeightedOitAccumulationFormat
                : colorFormat;
            renderingColorFormats[1] = Njulf.Rendering.Resources.RenderTargetManager.WeightedOitRevealageFormat;
            var renderingInfo = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = hasColorAttachment ? weightedOit ? 2u : 1u : 0u,
                PColorAttachmentFormats = hasColorAttachment ? renderingColorFormats : null,
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

        private static PipelineColorBlendAttachmentState AlphaBlendAttachment(bool blendEnable)
        {
            return new PipelineColorBlendAttachmentState
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
        }

        private static PipelineColorBlendAttachmentState AdditiveBlendAttachment()
        {
            return new PipelineColorBlendAttachmentState
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
        }

        private static PipelineColorBlendAttachmentState RevealageBlendAttachment()
        {
            return new PipelineColorBlendAttachmentState
            {
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.Zero,
                DstColorBlendFactor = BlendFactor.OneMinusSrcColor,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.Zero,
                DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = ColorComponentFlags.RBit |
                                 ColorComponentFlags.GBit |
                                 ColorComponentFlags.BBit |
                                 ColorComponentFlags.ABit
            };
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

            if (_forwardDepthWritePipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _forwardDepthWritePipeline, null);
                _forwardDepthWritePipeline = default;
            }

            if (_forwardValidatePipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _forwardValidatePipeline, null);
                _forwardValidatePipeline = default;
            }

            ulong transparentHandle = _transparentForwardPipeline.Handle;
            ulong weightedHandle = _weightedOitTransparentPipeline.Handle;

            if (transparentHandle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _transparentForwardPipeline, null);
                _transparentForwardPipeline = default;
            }

            if (weightedHandle != 0 && weightedHandle != transparentHandle)
            {
                _context.Api.DestroyPipeline(_context.Device, _weightedOitTransparentPipeline, null);
                _weightedOitTransparentPipeline = default;
            }
            else
            {
                _weightedOitTransparentPipeline = default;
            }

            if (_motionVectorPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _motionVectorPipeline, null);
                _motionVectorPipeline = default;
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

            if (_pipelineCache.Handle != 0)
                _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);

            if (_entryPointName != 0)
                SilkMarshal.Free(_entryPointName);

            System.Diagnostics.Debug.WriteLine("Mesh pipelines disposed.");
        }
    }
}
