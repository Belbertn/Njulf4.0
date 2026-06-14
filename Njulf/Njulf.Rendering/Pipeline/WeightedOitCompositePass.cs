using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline
{
    public sealed unsafe class WeightedOitCompositePass : RenderPassBase
    {
        private const string EntryPoint = "main";

        private readonly RenderTargetManager _renderTargets;
        private readonly nint _entryPointName;
        private PipelineLayout _pipelineLayout;
        private PipelineCache _pipelineCache;
        private VkPipeline _pipeline;

        public WeightedOitCompositePass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderTargetManager renderTargets)
            : base("WeightedOitCompositePass", context, swapchain, bindlessHeap)
        {
            _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
            _entryPointName = SilkMarshal.StringToPtr(EntryPoint);
        }

        public override void Initialize()
        {
            GraphicsPipelineFactory.ValidatePushConstantRange(
                _context,
                (uint)Marshal.SizeOf<GPUWeightedOitCompositePushConstants>(),
                "Weighted OIT composite pass");
            _pipelineCache = GraphicsPipelineFactory.CreatePipelineCache(_context, "Weighted OIT Composite Pipeline Cache");
            CreatePipelineLayout();
            _pipeline = CreatePipeline();
        }

        public override void DeclareResources(RenderGraphResourceRegistry resources)
        {
            if (resources == null)
                throw new ArgumentNullException(nameof(resources));

            RenderGraphResourceHandle sceneColor = ProductionRenderGraphResources.HdrSceneColor(resources);
            RenderGraphResourceHandle accumulation = ProductionRenderGraphResources.WeightedOitAccumulation(resources);
            RenderGraphResourceHandle revealage = ProductionRenderGraphResources.WeightedOitRevealage(resources);

            resources.AddPass(new RenderGraphPassDesc(Name, RenderGraphQueueClass.Graphics)
            {
                TimingLabel = Name,
                HasExternalSideEffect = true,
                NeverCull = true
            }
                .After("TransparentForwardPass")
                .Read(
                    accumulation,
                    RenderGraphResourceAccess.SampledRead,
                    PipelineStageFlags2.FragmentShaderBit)
                .Read(
                    revealage,
                    RenderGraphResourceAccess.SampledRead,
                    PipelineStageFlags2.FragmentShaderBit)
                .ReadWrite(
                    sceneColor,
                    RenderGraphResourceAccess.ColorAttachmentWrite,
                    PipelineStageFlags2.ColorAttachmentOutputBit,
                    AttachmentLoadOp.Load,
                    AttachmentStoreOp.Store));
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            return _context.IndependentBlendSupported && FramePassRuntimePolicy.ShouldExecute(Name, sceneData);
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            Extent2D renderExtent = _renderTargets.SceneColor.Extent;
            SetFullViewportAndScissor(cmd, renderExtent);
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline);
            BindBindlessStorageAndTextures(cmd, _pipelineLayout);

            var pushConstants = new GPUWeightedOitCompositePushConstants
            {
                AccumulationTextureIndex = BindlessIndex.WeightedOitAccumulationTexture,
                RevealageTextureIndex = BindlessIndex.WeightedOitRevealageTexture,
                DebugViewMode = (uint)sceneData.TransparencyDebugView
            };

            _context.Api.CmdPushConstants(
                cmd,
                _pipelineLayout,
                ShaderStageFlags.FragmentBit,
                0,
                (uint)Marshal.SizeOf<GPUWeightedOitCompositePushConstants>(),
                &pushConstants);

            var colorAttachment = ColorAttachment(
                _renderTargets.SceneColor.View,
                ImageLayout.ColorAttachmentOptimal,
                AttachmentLoadOp.Load,
                AttachmentStoreOp.Store);

            var renderingInfo = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D { Offset = new Offset2D { X = 0, Y = 0 }, Extent = renderExtent },
                LayerCount = 1,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorAttachment,
                PDepthAttachment = null,
                PStencilAttachment = null
            };

            _context.KhrDynamicRendering.CmdBeginRendering(cmd, &renderingInfo);
            _context.Api.CmdDraw(cmd, 3, 1, 0, 0);
            _context.KhrDynamicRendering.CmdEndRendering(cmd);

            sceneData.WeightedOitEnabled = true;
            sceneData.WeightedOitWidth = renderExtent.Width;
            sceneData.WeightedOitHeight = renderExtent.Height;
            sceneData.WeightedOitAccumulationFormat = RenderTargetManager.WeightedOitAccumulationFormat.ToString();
            sceneData.WeightedOitRevealageFormat = RenderTargetManager.WeightedOitRevealageFormat.ToString();
            sceneData.WeightedOitRenderTargetBytes = _renderTargets.WeightedOitRenderTargetBytes;
        }

        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            yield break;
        }

        public override void OnSwapchainRecreated()
        {
        }

        public override void Cleanup()
        {
            if (_pipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _pipeline, null);
                _pipeline = default;
            }

            if (_pipelineLayout.Handle != 0)
            {
                _context.Api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
                _pipelineLayout = default;
            }

            if (_pipelineCache.Handle != 0)
            {
                _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);
                _pipelineCache = default;
            }

            if (_entryPointName != 0)
                SilkMarshal.Free(_entryPointName);
        }

        private void CreatePipelineLayout()
        {
            var pushConstantRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.FragmentBit,
                Offset = 0,
                Size = (uint)Marshal.SizeOf<GPUWeightedOitCompositePushConstants>()
            };

            _pipelineLayout = GraphicsPipelineFactory.CreateBindlessPipelineLayout(
                _context,
                _bindlessHeap,
                pushConstantRange,
                "Weighted OIT Composite Pipeline Layout");
        }

        private VkPipeline CreatePipeline()
        {
            ShaderModule vertexModule = default;
            ShaderModule fragmentModule = default;
            try
            {
                vertexModule = ShaderModuleLoader.Load(_context, "composite.vert.spv");
                fragmentModule = ShaderModuleLoader.Load(_context, "weighted_oit_composite.frag.spv");
                _context.SetDebugName(vertexModule.Handle, ObjectType.ShaderModule, "composite.vert.spv");
                _context.SetDebugName(fragmentModule.Handle, ObjectType.ShaderModule, "weighted_oit_composite.frag.spv");
                return CreateGraphicsPipeline(vertexModule, fragmentModule);
            }
            finally
            {
                if (fragmentModule.Handle != 0)
                    _context.Api.DestroyShaderModule(_context.Device, fragmentModule, null);
                if (vertexModule.Handle != 0)
                    _context.Api.DestroyShaderModule(_context.Device, vertexModule, null);
            }
        }

        private VkPipeline CreateGraphicsPipeline(ShaderModule vertexModule, ShaderModule fragmentModule)
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
            var renderingColorFormat = RenderTargetManager.SceneColorFormat;
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
                Layout = _pipelineLayout,
                RenderPass = default,
                Subpass = 0,
                BasePipelineHandle = default,
                BasePipelineIndex = -1
            };

            Result result = _context.Api.CreateGraphicsPipelines(_context.Device, _pipelineCache, 1, &pipelineInfo, null, out VkPipeline pipeline);
            if (result != Result.Success)
                throw new VulkanException("Failed to create weighted OIT composite graphics pipeline", result);
            _context.SetDebugName(pipeline.Handle, ObjectType.Pipeline, "Weighted OIT Composite Pipeline");
            return pipeline;
        }
    }
}
