using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using static Njulf.Rendering.RenderingConstants;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline
{
    public sealed unsafe class DebugDrawPass : RenderPassBase
    {
        private const string EntryPoint = "main";
        private const int InitialVertexCapacity = 4096;

        private readonly BufferManager _bufferManager;
        private readonly StagingRing _stagingRing;
        private readonly RenderTargetManager _renderTargets;
        private readonly List<GPUDebugLineVertex> _vertices = new();
        private readonly nint _entryPointName;
        private readonly DebugVertexBuffer[] _vertexBuffers = new DebugVertexBuffer[FramesInFlight];

        private PipelineLayout _pipelineLayout;
        private PipelineCache _pipelineCache;
        private VkPipeline _depthTestedPipeline;
        private VkPipeline _overlayPipeline;

        public DebugDrawPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            BufferManager bufferManager,
            StagingRing stagingRing,
            RenderTargetManager renderTargets)
            : base("DebugDrawPass", context, swapchain, bindlessHeap)
        {
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _stagingRing = stagingRing ?? throw new ArgumentNullException(nameof(stagingRing));
            _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
            _entryPointName = SilkMarshal.StringToPtr(EntryPoint);
        }

        public override void Initialize()
        {
            _pipelineCache = GraphicsPipelineFactory.CreatePipelineCache(_context, "Debug Draw Pipeline Cache");
            CreatePipelineLayout();
            _depthTestedPipeline = CreatePipeline(depthTestEnabled: true, "Debug Draw Depth-Tested Pipeline");
            _overlayPipeline = CreatePipeline(depthTestEnabled: false, "Debug Draw Overlay Pipeline");

            for (int i = 0; i < _vertexBuffers.Length; i++)
                _vertexBuffers[i] = CreateVertexBuffer(InitialVertexCapacity, $"DebugDraw.VertexBuffer.Frame{i}");
        }

        public override void DeclareResources(RenderGraphResourceRegistry resources)
        {
            if (resources == null)
                throw new ArgumentNullException(nameof(resources));

            RenderGraphResourceHandle sceneColor = ProductionRenderGraphResources.HdrSceneColor(resources);
            RenderGraphResourceHandle sceneDepth = ProductionRenderGraphResources.SceneDepth(resources, _swapchain.DepthFormat);
            RenderGraphResourceHandle debugVertices = ProductionRenderGraphResources.DebugDrawVertexBuffer(resources);

            resources.AddPass(new RenderGraphPassDesc(Name, RenderGraphQueueClass.Graphics)
            {
                TimingLabel = Name,
                HasExternalSideEffect = true
            }
                .After("ParticlePass")
                .ReadWrite(
                    sceneColor,
                    RenderGraphResourceAccess.ColorAttachmentWrite,
                    PipelineStageFlags2.ColorAttachmentOutputBit,
                    AttachmentLoadOp.Load,
                    AttachmentStoreOp.Store)
                .Read(
                    sceneDepth,
                    RenderGraphResourceAccess.DepthStencilAttachmentRead,
                    PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit)
                .Read(
                    debugVertices,
                    RenderGraphResourceAccess.VertexBufferRead,
                    PipelineStageFlags2.VertexAttributeInputBit));
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            return sceneData.DebugToolingEnabled &&
                   sceneData.DebugDrawSnapshot.LineCount > 0 &&
                   sceneData.DebugDrawSnapshot.Lines.Count > 0;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            DebugDrawFrameSnapshot snapshot = sceneData.DebugDrawSnapshot;
            if (snapshot.LineCount <= 0 || snapshot.Lines.Count == 0)
                return;

            long buildStart = System.Diagnostics.Stopwatch.GetTimestamp();
            BuildVertices(snapshot, out int depthTestedVertexCount, out int overlayVertexCount);
            sceneData.CpuDebugDrawBuildMicroseconds = ElapsedMicroseconds(buildStart);
            if (_vertices.Count == 0)
                return;

            long recordStart = System.Diagnostics.Stopwatch.GetTimestamp();
            int safeFrameIndex = Math.Clamp(frameIndex, 0, FramesInFlight - 1);
            EnsureCapacity(safeFrameIndex, _vertices.Count);
            UploadVertices(cmd, safeFrameIndex);

            Extent2D targetExtent = _renderTargets.SceneColor.Extent;
            SetFullViewportAndScissor(cmd, targetExtent);

            var colorAttachment = ColorAttachment(
                _renderTargets.SceneColor.View,
                ImageLayout.ColorAttachmentOptimal,
                AttachmentLoadOp.Load,
                AttachmentStoreOp.Store);
            var depthAttachment = DepthAttachment(
                _renderTargets.SceneDepth.View,
                ImageLayout.DepthStencilReadOnlyOptimal,
                AttachmentLoadOp.Load,
                AttachmentStoreOp.DontCare);
            var renderingInfo = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D { Offset = new Offset2D { X = 0, Y = 0 }, Extent = targetExtent },
                LayerCount = 1,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorAttachment,
                PDepthAttachment = &depthAttachment,
                PStencilAttachment = null
            };

            _context.KhrDynamicRendering.CmdBeginRendering(cmd, &renderingInfo);
            BindAndPush(cmd, safeFrameIndex, sceneData);

            uint firstVertex = 0;
            if (depthTestedVertexCount > 0)
            {
                _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _depthTestedPipeline);
                _context.Api.CmdDraw(cmd, (uint)depthTestedVertexCount, 1, firstVertex, 0);
                firstVertex += (uint)depthTestedVertexCount;
            }

            if (overlayVertexCount > 0)
            {
                _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _overlayPipeline);
                _context.Api.CmdDraw(cmd, (uint)overlayVertexCount, 1, firstVertex, 0);
            }

            _context.KhrDynamicRendering.CmdEndRendering(cmd);
            sceneData.CpuDebugDrawRecordMicroseconds = ElapsedMicroseconds(recordStart);
        }

        public override void OnSwapchainRecreated()
        {
            DestroyPipeline(_depthTestedPipeline);
            DestroyPipeline(_overlayPipeline);
            _depthTestedPipeline = CreatePipeline(depthTestEnabled: true, "Debug Draw Depth-Tested Pipeline");
            _overlayPipeline = CreatePipeline(depthTestEnabled: false, "Debug Draw Overlay Pipeline");
        }

        public override void Cleanup()
        {
            DestroyPipeline(_depthTestedPipeline);
            DestroyPipeline(_overlayPipeline);
            _depthTestedPipeline = default;
            _overlayPipeline = default;

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

            for (int i = 0; i < _vertexBuffers.Length; i++)
            {
                if (_vertexBuffers[i].Handle.IsValid)
                    _bufferManager.DestroyBuffer(_vertexBuffers[i].Handle);
                _vertexBuffers[i] = default;
            }

            if (_entryPointName != 0)
                SilkMarshal.Free(_entryPointName);
        }

        private void CreatePipelineLayout()
        {
            var pushRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.VertexBit,
                Offset = 0,
                Size = (uint)Marshal.SizeOf<Matrix4x4PushConstants>()
            };
            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushRange
            };

            Result result = _context.Api.CreatePipelineLayout(_context.Device, &layoutInfo, null, out _pipelineLayout);
            if (result != Result.Success)
                throw new VulkanException("Failed to create debug draw pipeline layout", result);
            _context.SetDebugName(_pipelineLayout.Handle, ObjectType.PipelineLayout, "Debug Draw Pipeline Layout");
        }

        private VkPipeline CreatePipeline(bool depthTestEnabled, string debugName)
        {
            ShaderModule vertexModule = default;
            ShaderModule fragmentModule = default;
            try
            {
                vertexModule = ShaderModuleLoader.Load(_context, "debug_line.vert.spv");
                fragmentModule = ShaderModuleLoader.Load(_context, "debug_line.frag.spv");
                var shaderStages = stackalloc PipelineShaderStageCreateInfo[2]
                {
                    GraphicsPipelineFactory.ShaderStage(ShaderStageFlags.VertexBit, vertexModule, _entryPointName),
                    GraphicsPipelineFactory.ShaderStage(ShaderStageFlags.FragmentBit, fragmentModule, _entryPointName)
                };

                var binding = new VertexInputBindingDescription
                {
                    Binding = 0,
                    Stride = (uint)Marshal.SizeOf<GPUDebugLineVertex>(),
                    InputRate = VertexInputRate.Vertex
                };
                var attributes = stackalloc VertexInputAttributeDescription[2];
                attributes[0] = new VertexInputAttributeDescription
                {
                    Location = 0,
                    Binding = 0,
                    Format = Format.R32G32B32Sfloat,
                    Offset = (uint)Marshal.OffsetOf<GPUDebugLineVertex>(nameof(GPUDebugLineVertex.Position))
                };
                attributes[1] = new VertexInputAttributeDescription
                {
                    Location = 1,
                    Binding = 0,
                    Format = Format.R32G32B32A32Sfloat,
                    Offset = (uint)Marshal.OffsetOf<GPUDebugLineVertex>(nameof(GPUDebugLineVertex.Color))
                };
                var vertexInput = new PipelineVertexInputStateCreateInfo
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo,
                    VertexBindingDescriptionCount = 1,
                    PVertexBindingDescriptions = &binding,
                    VertexAttributeDescriptionCount = 2,
                    PVertexAttributeDescriptions = attributes
                };
                var inputAssembly = new PipelineInputAssemblyStateCreateInfo
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = PrimitiveTopology.LineList
                };
                var viewportState = GraphicsPipelineFactory.DynamicViewportScissorState();
                var rasterizationState = GraphicsPipelineFactory.FillNoCullRasterization();
                var multisampleState = GraphicsPipelineFactory.SingleSample();
                var depthStencilState = new PipelineDepthStencilStateCreateInfo
                {
                    SType = StructureType.PipelineDepthStencilStateCreateInfo,
                    DepthTestEnable = depthTestEnabled,
                    DepthWriteEnable = false,
                    DepthCompareOp = CompareOp.GreaterOrEqual
                };
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
                var colorBlendState = new PipelineColorBlendStateCreateInfo
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    AttachmentCount = 1,
                    PAttachments = &colorBlendAttachment
                };
                var dynamicStates = stackalloc DynamicState[2];
                var dynamicState = GraphicsPipelineFactory.DynamicViewportScissor(dynamicStates);
                var colorFormat = RenderTargetManager.SceneColorFormat;
                var renderingInfo = new PipelineRenderingCreateInfo
                {
                    SType = StructureType.PipelineRenderingCreateInfo,
                    ColorAttachmentCount = 1,
                    PColorAttachmentFormats = &colorFormat,
                    DepthAttachmentFormat = _swapchain.DepthFormat
                };

                var pipelineInfo = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    PNext = &renderingInfo,
                    StageCount = 2,
                    PStages = shaderStages,
                    PVertexInputState = &vertexInput,
                    PInputAssemblyState = &inputAssembly,
                    PViewportState = &viewportState,
                    PRasterizationState = &rasterizationState,
                    PMultisampleState = &multisampleState,
                    PDepthStencilState = &depthStencilState,
                    PColorBlendState = &colorBlendState,
                    PDynamicState = &dynamicState,
                    Layout = _pipelineLayout
                };

                Result result = _context.Api.CreateGraphicsPipelines(
                    _context.Device,
                    _pipelineCache,
                    1,
                    &pipelineInfo,
                    null,
                    out VkPipeline pipeline);
                if (result != Result.Success)
                    throw new VulkanException($"Failed to create {debugName}", result);
                _context.SetDebugName(pipeline.Handle, ObjectType.Pipeline, debugName);
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

        private void BuildVertices(DebugDrawFrameSnapshot snapshot, out int depthTestedVertexCount, out int overlayVertexCount)
        {
            _vertices.Clear();
            foreach (DebugDrawCommand command in snapshot.Lines)
            {
                if (command.DepthMode == DebugDrawDepthMode.DepthTested ||
                    command.DepthMode == DebugDrawDepthMode.XRay)
                {
                    AddLine(command.Line, command.Line.Color);
                }
            }

            depthTestedVertexCount = _vertices.Count;

            foreach (DebugDrawCommand command in snapshot.Lines)
            {
                if (command.DepthMode == DebugDrawDepthMode.AlwaysVisible)
                {
                    AddLine(command.Line, command.Line.Color);
                }
                else if (command.DepthMode == DebugDrawDepthMode.XRay)
                {
                    Vector4 color = command.Line.Color;
                    color.W = MathF.Min(MathF.Max(color.W, 0.0f), 0.35f);
                    AddLine(command.Line, color);
                }
            }

            overlayVertexCount = _vertices.Count - depthTestedVertexCount;
        }

        private void AddLine(DebugLine line, Vector4 color)
        {
            _vertices.Add(new GPUDebugLineVertex { Position = line.A, Color = color });
            _vertices.Add(new GPUDebugLineVertex { Position = line.B, Color = color });
        }

        private void EnsureCapacity(int frameIndex, int vertexCount)
        {
            DebugVertexBuffer buffer = _vertexBuffers[frameIndex];
            if (vertexCount <= buffer.VertexCapacity)
                return;

            if (buffer.Handle.IsValid)
                _bufferManager.DestroyBuffer(buffer.Handle);

            int capacity = Math.Max(InitialVertexCapacity, buffer.VertexCapacity);
            while (capacity < vertexCount)
                capacity = checked(capacity * 2);
            _vertexBuffers[frameIndex] = CreateVertexBuffer(capacity, $"DebugDraw.VertexBuffer.Frame{frameIndex}");
        }

        private DebugVertexBuffer CreateVertexBuffer(int vertexCapacity, string debugName)
        {
            ulong byteSize = checked((ulong)vertexCapacity * (ulong)Marshal.SizeOf<GPUDebugLineVertex>());
            BufferHandle handle = _bufferManager.CreateDeviceBuffer(
                byteSize,
                BufferUsageFlags.VertexBufferBit | BufferUsageFlags.TransferDstBit,
                requireDeviceAddress: false,
                MemoryBudgetCategory.DiagnosticsAndDebug,
                debugName);
            return new DebugVertexBuffer(handle, vertexCapacity, byteSize);
        }

        private void UploadVertices(CommandBuffer commandBuffer, int frameIndex)
        {
            GpuBufferUploader.UploadSpanToBuffer(
                _context,
                _bufferManager,
                _stagingRing,
                commandBuffer,
                _vertexBuffers[frameIndex].Handle,
                CollectionsMarshal.AsSpan(_vertices),
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.VertexAttributeInputBit,
                    AccessFlags2.VertexAttributeReadBit));
        }

        private void BindAndPush(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            VkBuffer vertexBuffer = _bufferManager.GetBuffer(_vertexBuffers[frameIndex].Handle);
            ulong offset = 0;
            _context.Api.CmdBindVertexBuffers(cmd, 0, 1, &vertexBuffer, &offset);
            var pushConstants = new Matrix4x4PushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix
            };
            _context.Api.CmdPushConstants(
                cmd,
                _pipelineLayout,
                ShaderStageFlags.VertexBit,
                0,
                (uint)Marshal.SizeOf<Matrix4x4PushConstants>(),
                &pushConstants);
        }

        private void TransitionDepthForRead(CommandBuffer cmd)
        {
            if (_swapchain.DepthImageLayout == ImageLayout.DepthStencilReadOnlyOptimal)
                return;

            var range = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.DepthBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            };

            var barrier = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
                SrcAccessMask = AccessFlags2.DepthStencilAttachmentWriteBit,
                DstStageMask = PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
                DstAccessMask = AccessFlags2.DepthStencilAttachmentReadBit,
                OldLayout = _swapchain.DepthImageLayout,
                NewLayout = ImageLayout.DepthStencilReadOnlyOptimal,
                Image = _swapchain.DepthImage,
                SubresourceRange = range
            };
            var dependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                ImageMemoryBarrierCount = 1,
                PImageMemoryBarriers = &barrier
            };

            _context.Api.CmdPipelineBarrier2(cmd, &dependency);
            _swapchain.SetDepthImageLayout(ImageLayout.DepthStencilReadOnlyOptimal);
        }

        private static long ElapsedMicroseconds(long startTimestamp)
        {
            return System.Diagnostics.Stopwatch.GetElapsedTime(startTimestamp).Ticks / (TimeSpan.TicksPerMillisecond / 1000);
        }

        private void DestroyPipeline(VkPipeline pipeline)
        {
            if (pipeline.Handle != 0)
                _context.Api.DestroyPipeline(_context.Device, pipeline, null);
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct Matrix4x4PushConstants
        {
            public Matrix4x4 ViewProjectionMatrix;
        }

        private readonly record struct DebugVertexBuffer(BufferHandle Handle, int VertexCapacity, ulong ByteSize);
    }
}
