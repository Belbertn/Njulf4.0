using System;
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
    /// <summary>
    /// Bounded DDGI probe visualization. The CPU uploads only sampled logical
    /// identities; canonical state, admitted updates, scheduler generations,
    /// receiver publication, and sparse residency are resolved in the vertex
    /// shader without readback or a probe-pool scan.
    /// </summary>
    public sealed unsafe class SimpleDdgiProbeDebugPass : RenderPassBase
    {
        internal const int MaximumSampledProbeCount = 768;
        internal const uint SphereVertexCount = 8u * 2u * 3u;
        internal const uint RelocationVertexCount = SphereVertexCount * 2u + 2u;
        private const string EntryPoint = "main";

        private readonly BufferManager _bufferManager;
        private readonly StagingRing _stagingRing;
        private readonly RenderTargetManager _renderTargets;
        private readonly BufferHandle[] _instanceBuffers =
            new BufferHandle[FramesInFlight];
        private nint _entryPointName;

        private PipelineLayout _pipelineLayout;
        private PipelineCache _pipelineCache;
        private VkPipeline _sampledDepthPipeline;
        private VkPipeline _sampledOverlayPipeline;
        private VkPipeline _updateDepthPipeline;
        private VkPipeline _updateOverlayPipeline;
        private bool _initialized;

        public SimpleDdgiProbeDebugPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            BufferManager bufferManager,
            StagingRing stagingRing,
            RenderTargetManager renderTargets)
            : base("SimpleDdgiProbeDebugPass", context, swapchain, bindlessHeap)
        {
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _stagingRing = stagingRing ?? throw new ArgumentNullException(nameof(stagingRing));
            _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
            Array.Fill(_instanceBuffers, BufferHandle.Invalid);
        }

        public override void Initialize()
        {
            // Optional resources stay completely absent until a DDGI probe
            // overlay actually reaches this pass.
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
            => ShouldExecuteForFrame(sceneData);

        internal static bool ShouldExecuteForFrame(SceneRenderingData sceneData)
        {
            bool updateMode = IsUpdateMode(sceneData.DebugOverlayMode);
            if (!sceneData.DebugToolingEnabled ||
                (sceneData.DebugOverlayStatus.Availability !=
                    DebugOverlayAvailability.Rendered &&
                 !(updateMode && sceneData.DebugOverlayStatus.Availability ==
                    DebugOverlayAvailability.NoData)) ||
                !DebugOverlayCatalog.TryGet(
                    sceneData.DebugOverlayMode,
                    out DebugOverlayDescriptor descriptor) ||
                descriptor.RendererKind != DebugOverlayRendererKind.DdgiProbe)
            {
                return false;
            }

            return updateMode
                ? sceneData.DebugDdgiUpdateRecordCapacity > 0
                : sceneData.DebugDdgiProbeInstanceCount > 0;
        }

        public override void Execute(
            CommandBuffer cmd,
            int frameIndex,
            SceneRenderingData sceneData)
        {
            long recordStart = System.Diagnostics.Stopwatch.GetTimestamp();
            bool updateMode = IsUpdateMode(sceneData.DebugOverlayMode);
            int safeFrameIndex = Math.Clamp(frameIndex, 0, FramesInFlight - 1);
            int instanceCount = updateMode
                ? Math.Clamp(
                    sceneData.DebugDdgiUpdateRecordCapacity,
                    0,
                    MaximumSampledProbeCount)
                : Math.Clamp(
                    sceneData.DebugDdgiProbeInstanceCount,
                    0,
                    MaximumSampledProbeCount);
            if (instanceCount <= 0)
                return;

            EnsureInitialized();
            if (!updateMode)
                UploadInstances(cmd, safeFrameIndex, sceneData, instanceCount);

            Extent2D extent = _renderTargets.SceneColor.Extent;
            _renderTargets.SceneColor.TransitionToColorAttachment(cmd);
            _renderTargets.SceneDepth.TransitionToDepthReadOnly(cmd);
            SetFullViewportAndScissor(cmd, extent);

            var colorAttachment = ColorAttachment(
                _renderTargets.SceneColor.View,
                ImageLayout.ColorAttachmentOptimal,
                AttachmentLoadOp.Load,
                AttachmentStoreOp.Store);
            var depthAttachment = DepthAttachment(
                _renderTargets.SceneDepth.View,
                ImageLayout.DepthStencilReadOnlyOptimal,
                AttachmentLoadOp.Load,
                AttachmentStoreOp.Store);
            var renderingInfo = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D
                {
                    Offset = new Offset2D { X = 0, Y = 0 },
                    Extent = extent
                },
                LayerCount = 1,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorAttachment,
                PDepthAttachment = &depthAttachment
            };

            _context.KhrDynamicRendering.CmdBeginRendering(cmd, &renderingInfo);
            BindBindlessStorageAndTextures(cmd, _pipelineLayout);
            if (!updateMode)
            {
                VkBuffer instanceBuffer = _bufferManager.GetBuffer(
                    _instanceBuffers[safeFrameIndex]);
                ulong offset = 0;
                _context.Api.CmdBindVertexBuffers(
                    cmd,
                    0,
                    1,
                    &instanceBuffer,
                    &offset);
            }

            uint vertexCount = sceneData.DebugOverlayMode ==
                DebugOverlayMode.DdgiProbeRelocation
                    ? RelocationVertexCount
                    : SphereVertexCount;
            switch (sceneData.DebugOverlayDepthMode)
            {
                case DebugDrawDepthMode.AlwaysVisible:
                    Draw(cmd, sceneData, updateMode, depthTested: false,
                        xrayLayer: false, vertexCount, instanceCount,
                        safeFrameIndex);
                    break;
                case DebugDrawDepthMode.XRay:
                    Draw(cmd, sceneData, updateMode, depthTested: true,
                        xrayLayer: false, vertexCount, instanceCount,
                        safeFrameIndex);
                    Draw(cmd, sceneData, updateMode, depthTested: false,
                        xrayLayer: true, vertexCount, instanceCount,
                        safeFrameIndex);
                    break;
                default:
                    Draw(cmd, sceneData, updateMode, depthTested: true,
                        xrayLayer: false, vertexCount, instanceCount,
                        safeFrameIndex);
                    break;
            }

            _context.KhrDynamicRendering.CmdEndRendering(cmd);
            sceneData.CpuDebugOverlayRecordMicroseconds +=
                ElapsedMicroseconds(recordStart);
        }

        public override void OnSwapchainRecreated()
        {
            if (!_initialized)
                return;
            DestroyPipelines();
            CreatePipelines();
        }

        public override void Cleanup()
        {
            DestroyPipelines();
            if (_pipelineLayout.Handle != 0)
                _context.Api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
            if (_pipelineCache.Handle != 0)
                _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);
            _pipelineLayout = default;
            _pipelineCache = default;

            for (int frame = 0; frame < _instanceBuffers.Length; frame++)
            {
                if (_instanceBuffers[frame].IsValid)
                    _bufferManager.DestroyBuffer(_instanceBuffers[frame]);
                _instanceBuffers[frame] = BufferHandle.Invalid;
            }

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
                "Simple DDGI Probe Debug Pipeline Cache");
            CreatePipelineLayout();
            CreatePipelines();
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
                StageFlags = ShaderStageFlags.VertexBit,
                Offset = 0,
                Size = (uint)Marshal.SizeOf<DebugDdgiProbePushConstants>()
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
            {
                throw new VulkanException(
                    "Failed to create Simple-DDGI probe debug pipeline layout",
                    result);
            }
            _context.SetDebugName(
                _pipelineLayout.Handle,
                ObjectType.PipelineLayout,
                "Simple DDGI Probe Debug Pipeline Layout");
        }

        private void CreatePipelines()
        {
            _sampledDepthPipeline = CreatePipeline(
                updateMode: false,
                depthTestEnabled: true,
                "Simple DDGI Sampled Probe Debug Depth Pipeline");
            _sampledOverlayPipeline = CreatePipeline(
                updateMode: false,
                depthTestEnabled: false,
                "Simple DDGI Sampled Probe Debug Overlay Pipeline");
            _updateDepthPipeline = CreatePipeline(
                updateMode: true,
                depthTestEnabled: true,
                "Simple DDGI Update Debug Depth Pipeline");
            _updateOverlayPipeline = CreatePipeline(
                updateMode: true,
                depthTestEnabled: false,
                "Simple DDGI Update Debug Overlay Pipeline");
        }

        private VkPipeline CreatePipeline(
            bool updateMode,
            bool depthTestEnabled,
            string debugName)
        {
            ShaderModule vertexModule = default;
            ShaderModule fragmentModule = default;
            try
            {
                vertexModule = ShaderModuleLoader.Load(
                    _context,
                    updateMode
                        ? "debug_ddgi_update.vert.spv"
                        : "debug_ddgi_probe.vert.spv");
                fragmentModule = ShaderModuleLoader.Load(
                    _context,
                    "debug_ddgi_probe.frag.spv");
                var stages = stackalloc PipelineShaderStageCreateInfo[2]
                {
                    GraphicsPipelineFactory.ShaderStage(
                        ShaderStageFlags.VertexBit,
                        vertexModule,
                        _entryPointName),
                    GraphicsPipelineFactory.ShaderStage(
                        ShaderStageFlags.FragmentBit,
                        fragmentModule,
                        _entryPointName)
                };

                VertexInputBindingDescription binding = default;
                var attributes = stackalloc VertexInputAttributeDescription[4];
                var vertexInput = new PipelineVertexInputStateCreateInfo
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo
                };
                if (!updateMode)
                {
                    binding = new VertexInputBindingDescription
                    {
                        Binding = 0,
                        Stride = (uint)Marshal.SizeOf<GPUDdgiProbeDebugInstance>(),
                        InputRate = VertexInputRate.Instance
                    };
                    attributes[0] = Attribute(
                        0,
                        Format.R32G32B32A32Sfloat,
                        nameof(GPUDdgiProbeDebugInstance.LogicalPositionAndRadius));
                    attributes[1] = Attribute(
                        1,
                        Format.R32G32B32A32Uint,
                        nameof(GPUDdgiProbeDebugInstance.VolumeIndex));
                    attributes[2] = Attribute(
                        2,
                        Format.R32G32B32A32Uint,
                        nameof(GPUDdgiProbeDebugInstance.VirtualProbeIndex));
                    attributes[3] = Attribute(
                        3,
                        Format.R32G32B32A32Uint,
                        nameof(GPUDdgiProbeDebugInstance.SchedulerResourceGeneration));
                    vertexInput.VertexBindingDescriptionCount = 1;
                    vertexInput.PVertexBindingDescriptions = &binding;
                    vertexInput.VertexAttributeDescriptionCount = 4;
                    vertexInput.PVertexAttributeDescriptions = attributes;
                }

                var inputAssembly = new PipelineInputAssemblyStateCreateInfo
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = PrimitiveTopology.LineList
                };
                var viewport = GraphicsPipelineFactory.DynamicViewportScissorState();
                var raster = GraphicsPipelineFactory.FillNoCullRasterization();
                var multisample = GraphicsPipelineFactory.SingleSample();
                var depth = new PipelineDepthStencilStateCreateInfo
                {
                    SType = StructureType.PipelineDepthStencilStateCreateInfo,
                    DepthTestEnable = depthTestEnabled,
                    DepthWriteEnable = false,
                    DepthCompareOp = CompareOp.GreaterOrEqual
                };
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
                var dynamic = GraphicsPipelineFactory.DynamicViewportScissor(
                    dynamicStates);
                Format colorFormat = RenderTargetManager.SceneColorFormat;
                var rendering = new PipelineRenderingCreateInfo
                {
                    SType = StructureType.PipelineRenderingCreateInfo,
                    ColorAttachmentCount = 1,
                    PColorAttachmentFormats = &colorFormat,
                    DepthAttachmentFormat = _swapchain.DepthFormat
                };
                var pipelineInfo = new GraphicsPipelineCreateInfo
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
                    PDepthStencilState = &depth,
                    PColorBlendState = &blend,
                    PDynamicState = &dynamic,
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

        private static VertexInputAttributeDescription Attribute(
            uint location,
            Format format,
            string fieldName) =>
            new()
            {
                Location = location,
                Binding = 0,
                Format = format,
                Offset = (uint)Marshal.OffsetOf<GPUDdgiProbeDebugInstance>(fieldName)
            };

        private void UploadInstances(
            CommandBuffer cmd,
            int frameIndex,
            SceneRenderingData sceneData,
            int instanceCount)
        {
            if (!_instanceBuffers[frameIndex].IsValid)
            {
                ulong byteSize = checked(
                    (ulong)MaximumSampledProbeCount *
                    (ulong)Marshal.SizeOf<GPUDdgiProbeDebugInstance>());
                _instanceBuffers[frameIndex] = _bufferManager.CreateDeviceBuffer(
                    byteSize,
                    BufferUsageFlags.VertexBufferBit | BufferUsageFlags.TransferDstBit,
                    requireDeviceAddress: false,
                    MemoryBudgetCategory.DiagnosticsAndDebug,
                    $"SimpleDdgiProbeDebug.InstanceBuffer.Frame{frameIndex}");
            }

            ReadOnlySpan<GPUDdgiProbeDebugInstance> instances =
                sceneData.DebugDdgiProbeInstances.AsSpan(0, instanceCount);
            GpuBufferUploader.UploadSpanToBuffer(
                _context,
                _bufferManager,
                _stagingRing,
                cmd,
                _instanceBuffers[frameIndex],
                instances,
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.VertexAttributeInputBit,
                    AccessFlags2.VertexAttributeReadBit));
        }

        private void Draw(
            CommandBuffer cmd,
            SceneRenderingData sceneData,
            bool updateMode,
            bool depthTested,
            bool xrayLayer,
            uint vertexCount,
            int instanceCount,
            int frameIndex)
        {
            VkPipeline pipeline = updateMode
                ? (depthTested ? _updateDepthPipeline : _updateOverlayPipeline)
                : (depthTested ? _sampledDepthPipeline : _sampledOverlayPipeline);
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);
            var push = new DebugDdgiProbePushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                Mode = (uint)sceneData.DebugOverlayMode,
                SampledInstanceCount = checked((uint)Math.Max(
                    0,
                    sceneData.DebugDdgiProbeInstanceCount)),
                UpdateRecordCapacity = checked((uint)Math.Max(
                    0,
                    sceneData.DebugDdgiUpdateRecordCapacity)),
                SchedulerMode = (uint)sceneData.SimpleDdgiSchedulerMode,
                SchedulerFrameOffsetWords =
                    sceneData.DebugDdgiSchedulerFrameOffsetWords,
                SchedulerProbeStateOffsetWords =
                    sceneData.DebugDdgiSchedulerProbeStateOffsetWords,
                SchedulerCountersOffsetWords =
                    sceneData.DebugDdgiSchedulerCountersOffsetWords,
                SchedulerUpdateRecordsOffsetWords =
                    sceneData.DebugDdgiSchedulerUpdateRecordsOffsetWords,
                VolumeTableGeneration = sceneData.DebugDdgiVolumeTableGeneration,
                SchedulerResourceGeneration = sceneData.DebugDdgiSchedulerGeneration,
                ResidencyResourceGeneration = sceneData.DebugDdgiResidencyGeneration,
                XRayLayerAndFrameIndex =
                    checked((uint)frameIndex << 1) | (xrayLayer ? 1u : 0u),
                CameraPosition = sceneData.CameraPosition,
                LifecycleLatencyTarget = Math.Max(
                    1,
                    sceneData.SimpleDdgiProbeLifecycleLatencyTargetFrames)
            };
            _context.Api.CmdPushConstants(
                cmd,
                _pipelineLayout,
                ShaderStageFlags.VertexBit,
                0,
                (uint)Marshal.SizeOf<DebugDdgiProbePushConstants>(),
                &push);
            _context.Api.CmdDraw(
                cmd,
                vertexCount,
                checked((uint)instanceCount),
                0,
                0);
        }

        private void DestroyPipelines()
        {
            DestroyPipeline(_sampledDepthPipeline);
            DestroyPipeline(_sampledOverlayPipeline);
            DestroyPipeline(_updateDepthPipeline);
            DestroyPipeline(_updateOverlayPipeline);
            _sampledDepthPipeline = default;
            _sampledOverlayPipeline = default;
            _updateDepthPipeline = default;
            _updateOverlayPipeline = default;
        }

        private void DestroyPipeline(VkPipeline pipeline)
        {
            if (pipeline.Handle != 0)
                _context.Api.DestroyPipeline(_context.Device, pipeline, null);
        }

        private static bool IsUpdateMode(DebugOverlayMode mode) =>
            mode is DebugOverlayMode.DdgiUpdatedProbes or
                DebugOverlayMode.DdgiUpdateReasons;

        private static long ElapsedMicroseconds(long startTimestamp) =>
            System.Diagnostics.Stopwatch.GetElapsedTime(startTimestamp).Ticks /
            (TimeSpan.TicksPerMillisecond / 1000);

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        internal struct DebugDdgiProbePushConstants
        {
            public Matrix4x4 ViewProjectionMatrix;
            public uint Mode;
            public uint SampledInstanceCount;
            public uint UpdateRecordCapacity;
            public uint SchedulerMode;
            public uint SchedulerFrameOffsetWords;
            public uint SchedulerProbeStateOffsetWords;
            public uint SchedulerCountersOffsetWords;
            public uint SchedulerUpdateRecordsOffsetWords;
            public uint VolumeTableGeneration;
            public uint SchedulerResourceGeneration;
            public uint ResidencyResourceGeneration;
            public uint XRayLayerAndFrameIndex;
            public Vector3 CameraPosition;
            public float LifecycleLatencyTarget;
        }
    }
}
