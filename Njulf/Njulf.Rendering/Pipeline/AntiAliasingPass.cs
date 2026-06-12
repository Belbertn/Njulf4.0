using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
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
    public sealed unsafe class AntiAliasingPass : RenderPassBase
    {
        private const string EntryPoint = "main";

        private readonly RenderTargetManager _renderTargets;
        private readonly RenderSettings _settings;
        private readonly Func<bool> _smaaLookupsReady;
        private readonly nint _entryPointName;
        private DescriptorSetLayout[] _setLayouts = Array.Empty<DescriptorSetLayout>();
        private PipelineLayout _pipelineLayout;
        private PipelineCache _pipelineCache;
        private VkPipeline _fxaaPipeline;
        private VkPipeline _smaaEdgePipeline;
        private VkPipeline _smaaBlendWeightPipeline;
        private VkPipeline _smaaNeighborhoodPipeline;
        private VkPipeline _taaPipeline;
        private bool _taaHistoryValid;
        private bool _taaWriteHistoryA = true;

        public AntiAliasingPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderTargetManager renderTargets,
            RenderSettings settings,
            Func<bool> smaaLookupsReady)
            : base("AntiAliasingPass", context, swapchain, bindlessHeap)
        {
            _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _smaaLookupsReady = smaaLookupsReady ?? throw new ArgumentNullException(nameof(smaaLookupsReady));
            _entryPointName = SilkMarshal.StringToPtr(EntryPoint);
        }

        public override void Initialize()
        {
            CreatePipelineCache();
            CreatePipelineLayout();
            _fxaaPipeline = CreatePipeline("fxaa.frag.spv", _swapchain.SurfaceFormat, "FXAA Pipeline");
            _smaaEdgePipeline = CreatePipeline("smaa_edge.frag.spv", RenderTargetManager.SmaaEdgesFormat, "SMAA Edge Pipeline");
            _smaaBlendWeightPipeline = CreatePipeline("smaa_blend_weight.frag.spv", RenderTargetManager.SmaaBlendWeightsFormat, "SMAA Blend Weight Pipeline");
            _smaaNeighborhoodPipeline = CreatePipeline("smaa_neighborhood.frag.spv", _swapchain.SurfaceFormat, "SMAA Neighborhood Pipeline");
            _taaPipeline = CreateTaaPipeline();
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            AntiAliasingMode mode = _settings.AntiAliasing.EffectiveMode;
            sceneData.AntiAliasingMode = mode;
            sceneData.AntiAliasingDebugView = _settings.AntiAliasing.DebugView;
            sceneData.AntiAliasingWidth = mode == AntiAliasingMode.None ? 0u : _renderTargets.LdrSceneColor.Extent.Width;
            sceneData.AntiAliasingHeight = mode == AntiAliasingMode.None ? 0u : _renderTargets.LdrSceneColor.Extent.Height;
            sceneData.AntiAliasingInputFormat = mode == AntiAliasingMode.None ? string.Empty : RenderTargetManager.LdrSceneColorFormat.ToString();
            sceneData.AntiAliasingOutputFormat = mode == AntiAliasingMode.None ? _swapchain.SurfaceFormat.ToString() : _swapchain.SurfaceFormat.ToString();
            sceneData.SmaaLookupTexturesReady = _smaaLookupsReady() ? 1 : 0;
            sceneData.MotionVectorsEnabled = 0;

            if (mode == AntiAliasingMode.None)
            {
                _taaHistoryValid = false;
                return;
            }

            _renderTargets.LdrSceneColor.TransitionToShaderRead(cmd);

            if (mode == AntiAliasingMode.Fxaa)
            {
                _taaHistoryValid = false;
                long start = Stopwatch.GetTimestamp();
                RenderFullscreen(cmd, _fxaaPipeline, GetSwapchainView(sceneData, frameIndex), _swapchain.Extent, "FXAA");
                sceneData.CpuFxaaRecordMicroseconds = ElapsedMicroseconds(start);
                return;
            }

            if (mode == AntiAliasingMode.Taa)
            {
                long start = Stopwatch.GetTimestamp();
                RenderTaa(cmd, frameIndex, sceneData);
                sceneData.CpuFxaaRecordMicroseconds = ElapsedMicroseconds(start);
                sceneData.MotionVectorsEnabled = 0;
                return;
            }

            if (!_smaaLookupsReady())
            {
                _taaHistoryValid = false;
                long fallbackStart = Stopwatch.GetTimestamp();
                RenderFullscreen(cmd, _fxaaPipeline, GetSwapchainView(sceneData, frameIndex), _swapchain.Extent, "FXAA SMAA Fallback");
                sceneData.CpuFxaaRecordMicroseconds = ElapsedMicroseconds(fallbackStart);
                sceneData.AntiAliasingMode = AntiAliasingMode.Fxaa;
                return;
            }

            long stageStart = Stopwatch.GetTimestamp();
            _renderTargets.SmaaEdges.TransitionToColorAttachment(cmd);
            RenderFullscreen(cmd, _smaaEdgePipeline, _renderTargets.SmaaEdges.View, _renderTargets.SmaaEdges.Extent, "SMAA Edge Detection");
            _renderTargets.SmaaEdges.TransitionToShaderRead(cmd);
            sceneData.CpuSmaaEdgeRecordMicroseconds = ElapsedMicroseconds(stageStart);

            if (_settings.AntiAliasing.DebugView == AntiAliasingDebugView.SmaaEdges)
            {
                RenderFullscreen(cmd, _fxaaPipeline, GetSwapchainView(sceneData, frameIndex), _swapchain.Extent, "SMAA Edge Debug");
                return;
            }

            stageStart = Stopwatch.GetTimestamp();
            _renderTargets.SmaaBlendWeights.TransitionToColorAttachment(cmd);
            RenderFullscreen(cmd, _smaaBlendWeightPipeline, _renderTargets.SmaaBlendWeights.View, _renderTargets.SmaaBlendWeights.Extent, "SMAA Blend Weights");
            _renderTargets.SmaaBlendWeights.TransitionToShaderRead(cmd);
            sceneData.CpuSmaaBlendRecordMicroseconds = ElapsedMicroseconds(stageStart);

            if (_settings.AntiAliasing.DebugView == AntiAliasingDebugView.SmaaBlendWeights)
            {
                RenderFullscreen(cmd, _fxaaPipeline, GetSwapchainView(sceneData, frameIndex), _swapchain.Extent, "SMAA Blend Weight Debug");
                return;
            }

            stageStart = Stopwatch.GetTimestamp();
            RenderFullscreen(cmd, _smaaNeighborhoodPipeline, GetSwapchainView(sceneData, frameIndex), _swapchain.Extent, "SMAA Neighborhood Blend");
            sceneData.CpuSmaaNeighborhoodRecordMicroseconds = ElapsedMicroseconds(stageStart);
            _taaHistoryValid = false;
        }

        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            yield break;
        }

        public override void OnSwapchainRecreated()
        {
            DestroyPipeline(_fxaaPipeline);
            DestroyPipeline(_smaaNeighborhoodPipeline);
            DestroyPipeline(_taaPipeline);
            _fxaaPipeline = CreatePipeline("fxaa.frag.spv", _swapchain.SurfaceFormat, "FXAA Pipeline");
            _smaaNeighborhoodPipeline = CreatePipeline("smaa_neighborhood.frag.spv", _swapchain.SurfaceFormat, "SMAA Neighborhood Pipeline");
            _taaPipeline = CreateTaaPipeline();
            _taaHistoryValid = false;
            _taaWriteHistoryA = true;
        }

        public override void Cleanup()
        {
            DestroyPipeline(_fxaaPipeline);
            DestroyPipeline(_smaaEdgePipeline);
            DestroyPipeline(_smaaBlendWeightPipeline);
            DestroyPipeline(_smaaNeighborhoodPipeline);
            DestroyPipeline(_taaPipeline);
            _fxaaPipeline = default;
            _smaaEdgePipeline = default;
            _smaaBlendWeightPipeline = default;
            _smaaNeighborhoodPipeline = default;
            _taaPipeline = default;

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

        private ImageView GetSwapchainView(SceneRenderingData sceneData, int frameIndex)
        {
            uint imageIndex = sceneData.ImageIndex < _swapchain.ImageCount ? sceneData.ImageIndex : (uint)frameIndex;
            return _swapchain.ImageViews[imageIndex];
        }

        private void RenderFullscreen(CommandBuffer cmd, VkPipeline pipeline, ImageView targetView, Extent2D extent, string label)
        {
            _context.BeginDebugLabel(cmd, label);
            try
            {
                var viewport = new Viewport
                {
                    X = 0,
                    Y = 0,
                    Width = extent.Width,
                    Height = extent.Height,
                    MinDepth = 0.0f,
                    MaxDepth = 1.0f
                };

                var scissor = new Rect2D
                {
                    Offset = new Offset2D { X = 0, Y = 0 },
                    Extent = extent
                };

                _context.Api.CmdSetViewport(cmd, 0, 1, &viewport);
                _context.Api.CmdSetScissor(cmd, 0, 1, &scissor);
                _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);

                var textureSet = _bindlessHeap.TextureSamplerSet;
                _context.Api.CmdBindDescriptorSets(
                    cmd,
                    PipelineBindPoint.Graphics,
                    _pipelineLayout,
                    1,
                    1,
                    &textureSet,
                    0,
                    null);

                var pushConstants = CreatePushConstants();

                _context.Api.CmdPushConstants(
                    cmd,
                    _pipelineLayout,
                    ShaderStageFlags.FragmentBit,
                    0,
                    (uint)Marshal.SizeOf<GPUAntiAliasingPushConstants>(),
                    &pushConstants);

                var colorAttachment = new RenderingAttachmentInfo
                {
                    SType = StructureType.RenderingAttachmentInfo,
                    ImageView = targetView,
                    ImageLayout = ImageLayout.ColorAttachmentOptimal,
                    LoadOp = AttachmentLoadOp.Clear,
                    StoreOp = AttachmentStoreOp.Store,
                    ClearValue = new ClearValue(new ClearColorValue(0.0f, 0.0f, 0.0f, 1.0f))
                };

                var renderingInfo = new RenderingInfo
                {
                    SType = StructureType.RenderingInfo,
                    RenderArea = new Rect2D { Offset = new Offset2D { X = 0, Y = 0 }, Extent = extent },
                    LayerCount = 1,
                    ColorAttachmentCount = 1,
                    PColorAttachments = &colorAttachment,
                    PDepthAttachment = null,
                    PStencilAttachment = null
                };

                _context.KhrDynamicRendering.CmdBeginRendering(cmd, &renderingInfo);
                _context.Api.CmdDraw(cmd, 3, 1, 0, 0);
                _context.KhrDynamicRendering.CmdEndRendering(cmd);
            }
            finally
            {
                _context.EndDebugLabel(cmd);
            }
        }

        private void RenderTaa(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            RenderTarget historyRead = _taaWriteHistoryA ? _renderTargets.TaaHistoryB : _renderTargets.TaaHistoryA;
            RenderTarget historyWrite = _taaWriteHistoryA ? _renderTargets.TaaHistoryA : _renderTargets.TaaHistoryB;
            historyRead.TransitionToShaderRead(cmd);
            historyWrite.TransitionToColorAttachment(cmd);
            _bindlessHeap.RegisterTexture(
                BindlessIndex.TaaHistoryTexture,
                historyRead.View,
                _bindlessHeap.ScreenSampler,
                ImageLayout.ShaderReadOnlyOptimal);

            _context.BeginDebugLabel(cmd, "TAA Resolve");
            try
            {
                var viewport = new Viewport
                {
                    X = 0,
                    Y = 0,
                    Width = _swapchain.Extent.Width,
                    Height = _swapchain.Extent.Height,
                    MinDepth = 0.0f,
                    MaxDepth = 1.0f
                };

                var scissor = new Rect2D
                {
                    Offset = new Offset2D { X = 0, Y = 0 },
                    Extent = _swapchain.Extent
                };

                _context.Api.CmdSetViewport(cmd, 0, 1, &viewport);
                _context.Api.CmdSetScissor(cmd, 0, 1, &scissor);
                _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _taaPipeline);

                var textureSet = _bindlessHeap.TextureSamplerSet;
                _context.Api.CmdBindDescriptorSets(
                    cmd,
                    PipelineBindPoint.Graphics,
                    _pipelineLayout,
                    1,
                    1,
                    &textureSet,
                    0,
                    null);

                var pushConstants = CreatePushConstants(taaHistoryValid: _taaHistoryValid ? 1u : 0u);
                _context.Api.CmdPushConstants(
                    cmd,
                    _pipelineLayout,
                    ShaderStageFlags.FragmentBit,
                    0,
                    (uint)Marshal.SizeOf<GPUAntiAliasingPushConstants>(),
                    &pushConstants);

                var attachments = stackalloc RenderingAttachmentInfo[2];
                attachments[0] = new RenderingAttachmentInfo
                {
                    SType = StructureType.RenderingAttachmentInfo,
                    ImageView = GetSwapchainView(sceneData, frameIndex),
                    ImageLayout = ImageLayout.ColorAttachmentOptimal,
                    LoadOp = AttachmentLoadOp.Clear,
                    StoreOp = AttachmentStoreOp.Store,
                    ClearValue = new ClearValue(new ClearColorValue(0.0f, 0.0f, 0.0f, 1.0f))
                };
                attachments[1] = new RenderingAttachmentInfo
                {
                    SType = StructureType.RenderingAttachmentInfo,
                    ImageView = historyWrite.View,
                    ImageLayout = ImageLayout.ColorAttachmentOptimal,
                    LoadOp = AttachmentLoadOp.Clear,
                    StoreOp = AttachmentStoreOp.Store,
                    ClearValue = new ClearValue(new ClearColorValue(0.0f, 0.0f, 0.0f, 1.0f))
                };

                var renderingInfo = new RenderingInfo
                {
                    SType = StructureType.RenderingInfo,
                    RenderArea = new Rect2D { Offset = new Offset2D { X = 0, Y = 0 }, Extent = _swapchain.Extent },
                    LayerCount = 1,
                    ColorAttachmentCount = 2,
                    PColorAttachments = attachments,
                    PDepthAttachment = null,
                    PStencilAttachment = null
                };

                _context.KhrDynamicRendering.CmdBeginRendering(cmd, &renderingInfo);
                _context.Api.CmdDraw(cmd, 3, 1, 0, 0);
                _context.KhrDynamicRendering.CmdEndRendering(cmd);
            }
            finally
            {
                _context.EndDebugLabel(cmd);
            }

            _taaHistoryValid = true;
            _taaWriteHistoryA = !_taaWriteHistoryA;
        }

        private GPUAntiAliasingPushConstants CreatePushConstants(uint taaHistoryValid = 0u)
        {
            Extent2D sourceExtent = _renderTargets.LdrSceneColor.Extent;
            return new GPUAntiAliasingPushConstants
            {
                InputTextureIndex = BindlessIndex.LdrSceneColorTexture,
                SmaaEdgesTextureIndex = BindlessIndex.SmaaEdgesTexture,
                SmaaBlendWeightsTextureIndex = BindlessIndex.SmaaBlendWeightsTexture,
                SmaaAreaTextureIndex = BindlessIndex.SmaaAreaTexture,
                SmaaSearchTextureIndex = BindlessIndex.SmaaSearchTexture,
                SourceDimensions = new Vector2(sourceExtent.Width, sourceExtent.Height),
                InvSourceDimensions = new Vector2(1.0f / sourceExtent.Width, 1.0f / sourceExtent.Height),
                FxaaContrastThreshold = _settings.AntiAliasing.FxaaContrastThreshold,
                FxaaRelativeThreshold = _settings.AntiAliasing.FxaaRelativeThreshold,
                FxaaSubpixelBlending = _settings.AntiAliasing.FxaaSubpixelBlending,
                SmaaThreshold = _settings.AntiAliasing.EffectiveSmaaThreshold,
                SmaaMaxSearchSteps = (uint)_settings.AntiAliasing.EffectiveSmaaMaxSearchSteps,
                SmaaMaxSearchStepsDiagonal = (uint)_settings.AntiAliasing.EffectiveSmaaMaxSearchStepsDiagonal,
                SmaaCornerRounding = _settings.AntiAliasing.EffectiveSmaaCornerRounding,
                DebugView = (uint)_settings.AntiAliasing.DebugView,
                OutputToSrgb = IsSrgbFormat(_swapchain.SurfaceFormat) ? 0u : 1u,
                SmaaQuality = (uint)_settings.AntiAliasing.EffectiveSmaaQuality,
                SmaaDiagonalEnabled = _settings.AntiAliasing.EffectiveSmaaDiagonalEnabled ? 1u : 0u,
                SmaaCornerEnabled = _settings.AntiAliasing.EffectiveSmaaCornerEnabled ? 1u : 0u,
                TaaFeedbackMin = _settings.AntiAliasing.TaaFeedbackMin,
                TaaFeedbackMax = _settings.AntiAliasing.TaaFeedbackMax,
                TaaVelocityRejectionScale = _settings.AntiAliasing.TaaVelocityRejectionScale,
                TaaHistoryValid = taaHistoryValid
            };
        }

        private void CreatePipelineCache()
        {
            var cacheInfo = new PipelineCacheCreateInfo { SType = StructureType.PipelineCacheCreateInfo };
            Result result = _context.Api.CreatePipelineCache(_context.Device, &cacheInfo, null, out _pipelineCache);
            if (result != Result.Success)
                throw new VulkanException("Failed to create anti-aliasing pipeline cache", result);
            _context.SetDebugName(_pipelineCache.Handle, ObjectType.PipelineCache, "Anti-Aliasing Pipeline Cache");
        }

        private void CreatePipelineLayout()
        {
            _setLayouts = [_bindlessHeap.StorageBufferSetLayout, _bindlessHeap.TextureSamplerSetLayout];
            fixed (DescriptorSetLayout* setLayouts = _setLayouts)
            {
                var pushConstantRange = new PushConstantRange
                {
                    StageFlags = ShaderStageFlags.FragmentBit,
                    Offset = 0,
                    Size = (uint)Marshal.SizeOf<GPUAntiAliasingPushConstants>()
                };

                var layoutInfo = new PipelineLayoutCreateInfo
                {
                    SType = StructureType.PipelineLayoutCreateInfo,
                    SetLayoutCount = 2,
                    PSetLayouts = setLayouts,
                    PushConstantRangeCount = 1,
                    PPushConstantRanges = &pushConstantRange
                };

                Result result = _context.Api.CreatePipelineLayout(_context.Device, &layoutInfo, null, out _pipelineLayout);
                if (result != Result.Success)
                    throw new VulkanException("Failed to create anti-aliasing pipeline layout", result);
                _context.SetDebugName(_pipelineLayout.Handle, ObjectType.PipelineLayout, "Anti-Aliasing Pipeline Layout");
            }
        }

        private VkPipeline CreatePipeline(string fragmentShaderName, Format colorFormat, string debugName)
        {
            ShaderModule vertexModule = default;
            ShaderModule fragmentModule = default;
            try
            {
                vertexModule = ShaderModuleLoader.Load(_context, "composite.vert.spv");
                fragmentModule = ShaderModuleLoader.Load(_context, fragmentShaderName);
                VkPipeline pipeline = CreateGraphicsPipeline(vertexModule, fragmentModule, colorFormat, debugName);
                _context.SetDebugName(pipeline.Handle, ObjectType.Pipeline, debugName);
                return pipeline;
            }
            finally
            {
                if (fragmentModule.Handle != 0)
                    _context.Api.DestroyShaderModule(_context.Device, fragmentModule, null);
                if (vertexModule.Handle != 0)
                    _context.Api.DestroyShaderModule(_context.Device, vertexModule, null);
            }
        }

        private VkPipeline CreateTaaPipeline()
        {
            ShaderModule vertexModule = default;
            ShaderModule fragmentModule = default;
            try
            {
                vertexModule = ShaderModuleLoader.Load(_context, "composite.vert.spv");
                fragmentModule = ShaderModuleLoader.Load(_context, "taa_resolve.frag.spv");
                var colorFormats = stackalloc Format[2];
                colorFormats[0] = _swapchain.SurfaceFormat;
                colorFormats[1] = RenderTargetManager.LdrSceneColorFormat;
                VkPipeline pipeline = CreateGraphicsPipeline(vertexModule, fragmentModule, colorFormats, 2, "TAA Resolve Pipeline");
                _context.SetDebugName(pipeline.Handle, ObjectType.Pipeline, "TAA Resolve Pipeline");
                return pipeline;
            }
            finally
            {
                if (fragmentModule.Handle != 0)
                    _context.Api.DestroyShaderModule(_context.Device, fragmentModule, null);
                if (vertexModule.Handle != 0)
                    _context.Api.DestroyShaderModule(_context.Device, vertexModule, null);
            }
        }

        private VkPipeline CreateGraphicsPipeline(ShaderModule vertexModule, ShaderModule fragmentModule, Format colorFormat, string debugName)
        {
            var renderingColorFormat = colorFormat;
            return CreateGraphicsPipeline(vertexModule, fragmentModule, &renderingColorFormat, 1, debugName);
        }

        private VkPipeline CreateGraphicsPipeline(ShaderModule vertexModule, ShaderModule fragmentModule, Format* colorFormats, uint colorAttachmentCount, string debugName)
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
                CullMode = CullModeFlags.None,
                PolygonMode = PolygonMode.Fill,
                FrontFace = FrontFace.CounterClockwise,
                LineWidth = 1.0f
            };
            var multisampleInfo = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit
            };
            var colorBlendAttachments = stackalloc PipelineColorBlendAttachmentState[(int)colorAttachmentCount];
            for (int i = 0; i < colorAttachmentCount; i++)
            {
                colorBlendAttachments[i] = new PipelineColorBlendAttachmentState
                {
                    BlendEnable = false,
                    ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit
                };
            }

            var colorBlendInfo = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = colorAttachmentCount,
                PAttachments = colorBlendAttachments
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
            var renderingInfo = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = colorAttachmentCount,
                PColorAttachmentFormats = colorFormats
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
                PColorBlendState = &colorBlendInfo,
                PDynamicState = &dynamicInfo,
                Layout = _pipelineLayout,
                BasePipelineIndex = -1
            };

            Result result = _context.Api.CreateGraphicsPipelines(_context.Device, _pipelineCache, 1, &pipelineInfo, null, out VkPipeline pipeline);
            if (result != Result.Success)
                throw new VulkanException($"Failed to create anti-aliasing graphics pipeline '{debugName}'", result);

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

        private void DestroyPipeline(VkPipeline pipeline)
        {
            if (pipeline.Handle != 0)
                _context.Api.DestroyPipeline(_context.Device, pipeline, null);
        }

        private static bool IsSrgbFormat(Format format)
        {
            return format == Format.R8G8B8A8Srgb ||
                   format == Format.B8G8R8A8Srgb ||
                   format == Format.A8B8G8R8SrgbPack32;
        }

        private static long ElapsedMicroseconds(long startTimestamp)
        {
            return Stopwatch.GetElapsedTime(startTimestamp).Ticks / (TimeSpan.TicksPerMillisecond / 1000);
        }
    }
}
