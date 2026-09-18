using System;
using System.Runtime.InteropServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline.PipelineObjects
{
    /// <summary>
    /// Fullscreen triangle pipeline that fills the motion-vector target with
    /// far-plane background velocity before the geometry buckets overwrite it.
    /// The fill is gated to TAA: other motion-vector consumers (AMD shadow
    /// denoiser, directional shadow temporal accumulation, GTAO, DDGI
    /// residual passes) expect the clear-value zeros for uncovered pixels.
    /// </summary>
    internal sealed unsafe class MotionVectorBackgroundPipeline : IDisposable
    {
        private const string EntryPoint = "main";

        private readonly VulkanContext _context;
        private readonly BindlessHeap _bindlessHeap;
        private readonly nint _entryPointName;
        private readonly Format _colorFormat;
        private readonly Format _depthFormat;

        private VkPipeline _pipeline;
        private PipelineLayout _layout;
        private PipelineCache _pipelineCache;
        private bool _disposed;

        public MotionVectorBackgroundPipeline(
            VulkanContext context,
            BindlessHeap bindlessHeap,
            Format colorFormat,
            Format depthFormat)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bindlessHeap = bindlessHeap ?? throw new ArgumentNullException(nameof(bindlessHeap));
            _colorFormat = colorFormat;
            _depthFormat = depthFormat;
            _entryPointName = SilkMarshal.StringToPtr(EntryPoint);

            try
            {
                ValidatePushConstantRange((uint)Marshal.SizeOf<GPUMotionVectorPushConstants>());
            }
            catch (Exception initializationFailure)
            {
                // A failed constructor has not transferred ownership to the pass.
                try { Dispose(); }
                catch (Exception cleanupFailure) { initializationFailure.Data["Njulf.PipelineCleanupFailure"] = cleanupFailure; }
                throw;
            }
        }

        public VkPipeline Pipeline
        {
            get
            {
                Prepare();
                return _pipeline;
            }
        }

        public PipelineLayout Layout
        {
            get
            {
                Prepare();
                return _layout;
            }
        }

        internal void Prepare()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pipeline.Handle != 0)
                return;
            CreatePipelineCache();
            CreatePipelineLayout();
            CreatePipeline();
        }

        private void ValidatePushConstantRange(uint requiredSize)
        {
            var properties = new PhysicalDeviceProperties();
            _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice, &properties);
            if (requiredSize > properties.Limits.MaxPushConstantsSize)
                throw new VulkanException($"Motion vector background fill requires {requiredSize} bytes of push constants but GPU supports {properties.Limits.MaxPushConstantsSize}.");
        }

        private void CreatePipelineCache()
        {
            var cacheInfo = new PipelineCacheCreateInfo
            {
                SType = StructureType.PipelineCacheCreateInfo
            };

            Result result = _context.Api.CreatePipelineCache(_context.Device, &cacheInfo, null, out _pipelineCache);
            if (result != Result.Success)
                throw new VulkanException("Failed to create motion vector background pipeline cache", result);
            _context.SetDebugName(_pipelineCache.Handle, ObjectType.PipelineCache, "Motion Vector Background Pipeline Cache");
        }

        private void CreatePipelineLayout()
        {
            var setLayouts = stackalloc DescriptorSetLayout[2];
            setLayouts[0] = _bindlessHeap.StorageBufferSetLayout;
            setLayouts[1] = _bindlessHeap.TextureSamplerSetLayout;

            var pushConstantRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.FragmentBit,
                Offset = 0,
                Size = (uint)Marshal.SizeOf<GPUMotionVectorPushConstants>()
            };

            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 2,
                PSetLayouts = setLayouts,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushConstantRange
            };

            Result result = _context.Api.CreatePipelineLayout(_context.Device, &layoutInfo, null, out _layout);
            if (result != Result.Success)
                throw new VulkanException("Failed to create motion vector background pipeline layout", result);
            _context.SetDebugName(_layout.Handle, ObjectType.PipelineLayout, "Motion Vector Background Pipeline Layout");
        }

        private void CreatePipeline()
        {
            ShaderModule vertexModule = new ShaderModule();
            ShaderModule fragmentModule = new ShaderModule();

            try
            {
                vertexModule = ShaderModuleLoader.Load(_context, "composite.vert.spv");
                fragmentModule = ShaderModuleLoader.Load(_context, "motion_vector_background.frag.spv");
                _context.SetDebugName(vertexModule.Handle, ObjectType.ShaderModule, "composite.vert.spv");
                _context.SetDebugName(fragmentModule.Handle, ObjectType.ShaderModule, "motion_vector_background.frag.spv");

                _pipeline = CreateGraphicsPipeline(vertexModule, fragmentModule);
                _context.SetDebugName(_pipeline.Handle, ObjectType.Pipeline, "Motion Vector Background Pipeline");
            }
            finally
            {
                DestroyShaderModule(fragmentModule);
                DestroyShaderModule(vertexModule);
            }
        }

        private VkPipeline CreateGraphicsPipeline(ShaderModule vertexModule, ShaderModule fragmentModule)
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = CreateShaderStageInfo(ShaderStageFlags.VertexBit, vertexModule);
            stages[1] = CreateShaderStageInfo(ShaderStageFlags.FragmentBit, fragmentModule);

            var vertexInputInfo = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo };
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
            // The rendering instance carries the scene depth attachment; the
            // fill never reads or writes it.
            var depthStencilInfo = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = false,
                DepthWriteEnable = false,
                DepthBoundsTestEnable = false,
                StencilTestEnable = false
            };
            var colorBlendAttachment = new PipelineColorBlendAttachmentState
            {
                BlendEnable = false,
                ColorWriteMask = ColorComponentFlags.RBit |
                                 ColorComponentFlags.GBit |
                                 ColorComponentFlags.BBit |
                                 ColorComponentFlags.ABit
            };
            var colorBlendInfo = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = 1,
                PAttachments = &colorBlendAttachment
            };
            var dynamicStates = stackalloc DynamicState[2];
            dynamicStates[0] = DynamicState.Viewport;
            dynamicStates[1] = DynamicState.Scissor;
            var dynamicInfo = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2,
                PDynamicStates = dynamicStates
            };
            var renderingColorFormat = _colorFormat;
            var renderingInfo = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = 1,
                PColorAttachmentFormats = &renderingColorFormat,
                DepthAttachmentFormat = _depthFormat,
                StencilAttachmentFormat = Format.Undefined
            };
            var pipelineInfo = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = &renderingInfo,
                StageCount = 2,
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
                throw new VulkanException("Failed to create motion vector background graphics pipeline", result);

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
            if (_pipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _pipeline, null);
                _pipeline = default;
            }
            if (_layout.Handle != 0)
            {
                _context.Api.DestroyPipelineLayout(_context.Device, _layout, null);
                _layout = default;
            }
            if (_pipelineCache.Handle != 0)
            {
                _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);
                _pipelineCache = default;
            }
            if (_entryPointName != 0)
                SilkMarshal.Free(_entryPointName);
            GC.SuppressFinalize(this);
        }
    }
}
