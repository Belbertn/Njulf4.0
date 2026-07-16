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
    public sealed unsafe class WeightedOitCompositePipeline : IDisposable
    {
        private const string EntryPoint = "main";

        private readonly VulkanContext _context;
        private readonly BindlessHeap _bindlessHeap;
        private readonly nint _entryPointName;

        private VkPipeline _pipeline;
        private PipelineLayout _layout;
        private PipelineCache _pipelineCache;
        private bool _disposed;

        public WeightedOitCompositePipeline(VulkanContext context, BindlessHeap bindlessHeap, Format colorFormat)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bindlessHeap = bindlessHeap ?? throw new ArgumentNullException(nameof(bindlessHeap));
            _entryPointName = SilkMarshal.StringToPtr(EntryPoint);

            GraphicsPipelineFactory.ValidatePushConstantRange(
                _context,
                (uint)Marshal.SizeOf<GPUWeightedOitCompositePushConstants>(),
                "Weighted OIT composite pass");
            _pipelineCache = GraphicsPipelineFactory.CreatePipelineCache(_context, "Weighted OIT Composite Pipeline Cache");
            CreatePipelineLayout();
            CreatePipeline(colorFormat);
        }

        public VkPipeline Pipeline => _pipeline;
        public PipelineLayout Layout => _layout;

        public void Recreate(Format colorFormat)
        {
            DestroyPipeline();
            CreatePipeline(colorFormat);
        }

        private void CreatePipelineLayout()
        {
            var pushConstantRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.FragmentBit,
                Offset = 0,
                Size = (uint)Marshal.SizeOf<GPUWeightedOitCompositePushConstants>()
            };

            _layout = GraphicsPipelineFactory.CreateBindlessPipelineLayout(
                _context,
                _bindlessHeap,
                pushConstantRange,
                "Weighted OIT Composite Pipeline Layout");
        }

        private void CreatePipeline(Format colorFormat)
        {
            ShaderModule vertexModule = new ShaderModule();
            ShaderModule fragmentModule = new ShaderModule();

            try
            {
                vertexModule = ShaderModuleLoader.Load(_context, "composite.vert.spv");
                fragmentModule = ShaderModuleLoader.Load(_context, "weighted_oit_composite.frag.spv");
                _context.SetDebugName(vertexModule.Handle, ObjectType.ShaderModule, "composite.vert.spv");
                _context.SetDebugName(fragmentModule.Handle, ObjectType.ShaderModule, "weighted_oit_composite.frag.spv");

                _pipeline = CreateGraphicsPipeline(vertexModule, fragmentModule, colorFormat);
                _context.SetDebugName(_pipeline.Handle, ObjectType.Pipeline, "Weighted OIT Composite Pipeline");
            }
            finally
            {
                DestroyShaderModule(fragmentModule);
                DestroyShaderModule(vertexModule);
            }
        }

        private VkPipeline CreateGraphicsPipeline(ShaderModule vertexModule, ShaderModule fragmentModule, Format colorFormat)
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = GraphicsPipelineFactory.ShaderStage(ShaderStageFlags.VertexBit, vertexModule, _entryPointName);
            stages[1] = GraphicsPipelineFactory.ShaderStage(ShaderStageFlags.FragmentBit, fragmentModule, _entryPointName);

            var vertexInputInfo = GraphicsPipelineFactory.EmptyVertexInput();
            var inputAssemblyInfo = GraphicsPipelineFactory.TriangleListInputAssembly();
            var viewportInfo = GraphicsPipelineFactory.DynamicViewportScissorState();
            var rasterInfo = GraphicsPipelineFactory.FillNoCullRasterization();
            var multisampleInfo = GraphicsPipelineFactory.SingleSample();

            var colorBlendAttachment = new PipelineColorBlendAttachmentState
            {
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.SrcAlpha,
                DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
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
            var dynamicInfo = GraphicsPipelineFactory.DynamicViewportScissor(dynamicStates);

            var renderingColorFormat = colorFormat;
            var renderingInfo = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = 1,
                PColorAttachmentFormats = &renderingColorFormat,
                DepthAttachmentFormat = Format.Undefined,
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
                PDepthStencilState = null,
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
                throw new VulkanException("Failed to create weighted OIT composite graphics pipeline", result);

            return pipeline;
        }

        private void DestroyPipeline()
        {
            if (_pipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _pipeline, null);
                _pipeline = default;
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
            DestroyPipeline();
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
