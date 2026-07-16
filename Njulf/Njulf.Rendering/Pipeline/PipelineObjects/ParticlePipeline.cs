using System;
using System.Runtime.InteropServices;
using Njulf.Core.Vfx;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline.PipelineObjects
{
    public sealed unsafe class ParticlePipeline : IDisposable
    {
        private const string EntryPoint = "main";

        private readonly VulkanContext _context;
        private readonly BindlessHeap _bindlessHeap;
        private readonly nint _entryPointName;
        private VkPipeline _alphaPipeline;
        private VkPipeline _premultipliedPipeline;
        private VkPipeline _additivePipeline;
        private VkPipeline _softAdditivePipeline;
        private PipelineLayout _layout;
        private PipelineCache _pipelineCache;
        private bool _disposed;

        public ParticlePipeline(
            VulkanContext context,
            BindlessHeap bindlessHeap,
            Format colorFormat,
            Format depthFormat)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bindlessHeap = bindlessHeap ?? throw new ArgumentNullException(nameof(bindlessHeap));
            _entryPointName = SilkMarshal.StringToPtr(EntryPoint);

            ValidatePushConstantRange((uint)Marshal.SizeOf<GPUParticlePushConstants>());
            CreatePipelineCache();
            CreatePipelineLayout();
            CreatePipelines(colorFormat, depthFormat);
        }

        public PipelineLayout Layout => _layout;

        public VkPipeline GetPipeline(ParticleBlendMode blendMode)
        {
            return blendMode switch
            {
                ParticleBlendMode.AlphaBlend or ParticleBlendMode.AlphaClip => _alphaPipeline,
                ParticleBlendMode.Additive => _additivePipeline,
                ParticleBlendMode.SoftAdditive => _softAdditivePipeline,
                _ => _premultipliedPipeline
            };
        }

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
                throw new VulkanException($"Particle pass requires {requiredSize} bytes of push constants but GPU supports {properties.Limits.MaxPushConstantsSize}.");
        }

        private void CreatePipelineCache()
        {
            var cacheInfo = new PipelineCacheCreateInfo
            {
                SType = StructureType.PipelineCacheCreateInfo
            };

            Result result = _context.Api.CreatePipelineCache(_context.Device, &cacheInfo, null, out _pipelineCache);
            if (result != Result.Success)
                throw new VulkanException("Failed to create particle pipeline cache", result);
            _context.SetDebugName(_pipelineCache.Handle, ObjectType.PipelineCache, "Particle Pipeline Cache");
        }

        private void CreatePipelineLayout()
        {
            var setLayouts = stackalloc DescriptorSetLayout[2];
            setLayouts[0] = _bindlessHeap.StorageBufferSetLayout;
            setLayouts[1] = _bindlessHeap.TextureSamplerSetLayout;

            var pushConstantRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                Offset = 0,
                Size = (uint)Marshal.SizeOf<GPUParticlePushConstants>()
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
                throw new VulkanException("Failed to create particle pipeline layout", result);
            _context.SetDebugName(_layout.Handle, ObjectType.PipelineLayout, "Particle Pipeline Layout");
        }

        private void CreatePipelines(Format colorFormat, Format depthFormat)
        {
            ShaderModule vertexModule = default;
            ShaderModule fragmentModule = default;

            try
            {
                vertexModule = ShaderModuleLoader.Load(_context, "particle.vert.spv");
                fragmentModule = ShaderModuleLoader.Load(_context, "particle.frag.spv");
                _context.SetDebugName(vertexModule.Handle, ObjectType.ShaderModule, "particle.vert.spv");
                _context.SetDebugName(fragmentModule.Handle, ObjectType.ShaderModule, "particle.frag.spv");

                _alphaPipeline = CreateGraphicsPipeline(vertexModule, fragmentModule, colorFormat, depthFormat, BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha, "Particle Alpha Pipeline");
                _premultipliedPipeline = CreateGraphicsPipeline(vertexModule, fragmentModule, colorFormat, depthFormat, BlendFactor.One, BlendFactor.OneMinusSrcAlpha, "Particle Premultiplied Pipeline");
                _additivePipeline = CreateGraphicsPipeline(vertexModule, fragmentModule, colorFormat, depthFormat, BlendFactor.One, BlendFactor.One, "Particle Additive Pipeline");
                _softAdditivePipeline = CreateGraphicsPipeline(vertexModule, fragmentModule, colorFormat, depthFormat, BlendFactor.One, BlendFactor.One, "Particle Soft Additive Pipeline");
            }
            finally
            {
                DestroyShaderModule(fragmentModule);
                DestroyShaderModule(vertexModule);
            }
        }

        private VkPipeline CreateGraphicsPipeline(
            ShaderModule vertexModule,
            ShaderModule fragmentModule,
            Format colorFormat,
            Format depthFormat,
            BlendFactor srcColor,
            BlendFactor dstColor,
            string debugName)
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = CreateShaderStageInfo(ShaderStageFlags.VertexBit, vertexModule);
            stages[1] = CreateShaderStageInfo(ShaderStageFlags.FragmentBit, fragmentModule);

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

            var colorBlendAttachment = new PipelineColorBlendAttachmentState
            {
                BlendEnable = true,
                SrcColorBlendFactor = srcColor,
                DstColorBlendFactor = dstColor,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
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

            var renderingColorFormat = colorFormat;
            var renderingInfo = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = 1,
                PColorAttachmentFormats = &renderingColorFormat,
                DepthAttachmentFormat = depthFormat,
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

            Result result = _context.Api.CreateGraphicsPipelines(_context.Device, _pipelineCache, 1, &pipelineInfo, null, out VkPipeline pipeline);
            if (result != Result.Success)
                throw new VulkanException($"Failed to create {debugName}", result);

            _context.SetDebugName(pipeline.Handle, ObjectType.Pipeline, debugName);
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
            DestroyPipeline(ref _alphaPipeline);
            DestroyPipeline(ref _premultipliedPipeline);
            DestroyPipeline(ref _additivePipeline);
            DestroyPipeline(ref _softAdditivePipeline);
        }

        private void DestroyPipeline(ref VkPipeline pipeline)
        {
            if (pipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, pipeline, null);
                pipeline = default;
            }
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
            if (_layout.Handle != 0)
                _context.Api.DestroyPipelineLayout(_context.Device, _layout, null);
            if (_pipelineCache.Handle != 0)
                _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);
            if (_entryPointName != 0)
                SilkMarshal.Free(_entryPointName);
            GC.SuppressFinalize(this);
        }
    }
}
