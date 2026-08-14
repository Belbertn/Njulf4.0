using System;
using System.Runtime.InteropServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline
{
    /// <summary>
    /// Debug-only full-screen overlays. Resources and shaders are created
    /// lazily on first execution so normal and None frames pay no allocation,
    /// upload, dispatch, or draw cost.
    /// </summary>
    public sealed unsafe class DebugOverlayPass : RenderPassBase
    {
        private const string EntryPoint = "main";
        private readonly RenderTargetManager _renderTargets;
        private nint _entryPointName;
        private PipelineLayout _pipelineLayout;
        private PipelineCache _pipelineCache;
        private VkPipeline _pipeline;
        private bool _initialized;

        public DebugOverlayPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderTargetManager renderTargets)
            : base("DebugOverlayPass", context, swapchain, bindlessHeap)
        {
            _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
        }

        public override void Initialize()
        {
            // Deliberately lazy. Debug-disabled startup must allocate nothing
            // for this optional pass.
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData) =>
            ShouldExecuteForFrame(sceneData);

        internal static bool ShouldExecuteForFrame(SceneRenderingData sceneData) =>
            sceneData.DebugToolingEnabled &&
            sceneData.DebugOverlayMode == DebugOverlayMode.LightTiles &&
            sceneData.DebugOverlayStatus.Availability == DebugOverlayAvailability.Rendered &&
            sceneData.LocalLightCount > 0;

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            EnsureInitialized();
            _renderTargets.SceneColor.TransitionToColorAttachment(cmd);
            Extent2D extent = _renderTargets.SceneColor.Extent;
            SetFullViewportAndScissor(cmd, extent);

            var colorAttachment = ColorAttachment(
                _renderTargets.SceneColor.View,
                ImageLayout.ColorAttachmentOptimal,
                AttachmentLoadOp.Load,
                AttachmentStoreOp.Store);
            var renderingInfo = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D(new Offset2D(0, 0), extent),
                LayerCount = 1,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorAttachment
            };

            _context.KhrDynamicRendering.CmdBeginRendering(cmd, &renderingInfo);
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline);
            DescriptorSet storageSet = _bindlessHeap.StorageBufferSet;
            DescriptorSet textureSet = _bindlessHeap.TextureSamplerSet;
            _context.Api.CmdBindDescriptorSets(
                cmd, PipelineBindPoint.Graphics, _pipelineLayout,
                0, 1, &storageSet, 0, null);
            _context.Api.CmdBindDescriptorSets(
                cmd, PipelineBindPoint.Graphics, _pipelineLayout,
                1, 1, &textureSet, 0, null);

            var push = new DebugOverlayPushConstants
            {
                TileCountX = sceneData.TileCountX,
                TileCountY = sceneData.TileCountY,
                MaxLightsPerTile = checked((uint)Math.Max(1, sceneData.MaxLightsPerTile)),
                HeaderBufferIndex = BindlessIndex.TiledLightHeaderBuffer,
                ScreenWidth = extent.Width,
                ScreenHeight = extent.Height,
                LocalLightCount = checked((uint)Math.Max(0, sceneData.LocalLightCount))
            };
            _context.Api.CmdPushConstants(
                cmd,
                _pipelineLayout,
                ShaderStageFlags.FragmentBit,
                0,
                (uint)Marshal.SizeOf<DebugOverlayPushConstants>(),
                &push);
            _context.Api.CmdDraw(cmd, 3, 1, 0, 0);
            _context.KhrDynamicRendering.CmdEndRendering(cmd);
        }

        public override void OnSwapchainRecreated()
        {
            // SceneColor format is stable; dynamic rendering supplies extent.
        }

        public override void Cleanup()
        {
            if (_pipeline.Handle != 0)
                _context.Api.DestroyPipeline(_context.Device, _pipeline, null);
            if (_pipelineLayout.Handle != 0)
                _context.Api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
            if (_pipelineCache.Handle != 0)
                _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);
            _pipeline = default;
            _pipelineLayout = default;
            _pipelineCache = default;
            if (_entryPointName != 0)
                SilkMarshal.Free(_entryPointName);
            _entryPointName = 0;
            _initialized = false;
        }

        private void EnsureInitialized()
        {
            if (_initialized)
                return;

            if (_entryPointName == 0)
                _entryPointName = SilkMarshal.StringToPtr(EntryPoint);

            _pipelineCache = GraphicsPipelineFactory.CreatePipelineCache(
                _context,
                "Debug Overlay Pipeline Cache");
            CreatePipelineLayout();
            _pipeline = CreatePipeline();
            _initialized = true;
        }

        private void CreatePipelineLayout()
        {
            var layouts = stackalloc DescriptorSetLayout[2]
            {
                _bindlessHeap.StorageBufferSetLayout,
                _bindlessHeap.TextureSamplerSetLayout
            };
            var pushRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.FragmentBit,
                Offset = 0,
                Size = (uint)Marshal.SizeOf<DebugOverlayPushConstants>()
            };
            var info = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 2,
                PSetLayouts = layouts,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushRange
            };
            Result result = _context.Api.CreatePipelineLayout(
                _context.Device,
                &info,
                null,
                out _pipelineLayout);
            if (result != Result.Success)
                throw new VulkanException("Failed to create debug-overlay pipeline layout", result);
            _context.SetDebugName(
                _pipelineLayout.Handle,
                ObjectType.PipelineLayout,
                "Debug Overlay Pipeline Layout");
        }

        private VkPipeline CreatePipeline()
        {
            ShaderModule vertexModule = default;
            ShaderModule fragmentModule = default;
            try
            {
                vertexModule = ShaderModuleLoader.Load(_context, "debug_overlay.vert.spv");
                fragmentModule = ShaderModuleLoader.Load(_context, "debug_overlay.frag.spv");
                var stages = stackalloc PipelineShaderStageCreateInfo[2]
                {
                    GraphicsPipelineFactory.ShaderStage(
                        ShaderStageFlags.VertexBit, vertexModule, _entryPointName),
                    GraphicsPipelineFactory.ShaderStage(
                        ShaderStageFlags.FragmentBit, fragmentModule, _entryPointName)
                };
                var vertexInput = new PipelineVertexInputStateCreateInfo
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo
                };
                var inputAssembly = new PipelineInputAssemblyStateCreateInfo
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = PrimitiveTopology.TriangleList
                };
                var viewport = GraphicsPipelineFactory.DynamicViewportScissorState();
                var raster = GraphicsPipelineFactory.FillNoCullRasterization();
                var multisample = GraphicsPipelineFactory.SingleSample();
                var blendAttachment = new PipelineColorBlendAttachmentState
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
                var blend = new PipelineColorBlendStateCreateInfo
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    AttachmentCount = 1,
                    PAttachments = &blendAttachment
                };
                var dynamicStates = stackalloc DynamicState[2];
                var dynamic = GraphicsPipelineFactory.DynamicViewportScissor(dynamicStates);
                Format colorFormat = RenderTargetManager.SceneColorFormat;
                var rendering = new PipelineRenderingCreateInfo
                {
                    SType = StructureType.PipelineRenderingCreateInfo,
                    ColorAttachmentCount = 1,
                    PColorAttachmentFormats = &colorFormat
                };
                var info = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    PNext = &rendering,
                    StageCount = 2,
                    PStages = stages,
                    PVertexInputState = &vertexInput,
                    PInputAssemblyState = &inputAssembly,
                    PViewportState = &viewport,
                    PRasterizationState = &raster,
                    PMultisampleState = &multisample,
                    PColorBlendState = &blend,
                    PDynamicState = &dynamic,
                    Layout = _pipelineLayout
                };
                Result result = _context.Api.CreateGraphicsPipelines(
                    _context.Device,
                    _pipelineCache,
                    1,
                    &info,
                    null,
                    out VkPipeline pipeline);
                if (result != Result.Success)
                    throw new VulkanException("Failed to create debug-overlay pipeline", result);
                _context.SetDebugName(
                    pipeline.Handle,
                    ObjectType.Pipeline,
                    "Debug Overlay Light-Tile Pipeline");
                return pipeline;
            }
            finally
            {
                if (vertexModule.Handle != 0)
                    _context.Api.DestroyShaderModule(_context.Device, vertexModule, null);
                if (fragmentModule.Handle != 0)
                    _context.Api.DestroyShaderModule(_context.Device, fragmentModule, null);
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct DebugOverlayPushConstants
        {
            public uint TileCountX;
            public uint TileCountY;
            public uint MaxLightsPerTile;
            public uint HeaderBufferIndex;
            public uint ScreenWidth;
            public uint ScreenHeight;
            public uint LocalLightCount;
            public uint Padding0;
        }
    }
}
