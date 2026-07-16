using System.Numerics;
using System.Runtime.InteropServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
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

namespace Njulf.Rendering.Pipeline;

public sealed unsafe class ImGuiRenderPass : RenderPassBase
{
    private readonly BufferManager _buffers;
    private readonly StagingRing _staging;
    private readonly OverlayDrawDataSource _source;
    private readonly FrameBuffers[] _frames = new FrameBuffers[FramesInFlight];
    private nint _entry;
    private bool _initialized;
    private PipelineLayout _layout;
    private PipelineCache _cache;
    private VkPipeline _pipeline;

    internal ImGuiRenderPass(VulkanContext context, SwapchainManager swapchain, BindlessHeap heap, BufferManager buffers, StagingRing staging, OverlayDrawDataSource source)
        : base("ImGuiRenderPass", context, swapchain, heap) { _buffers = buffers; _staging = staging; _source = source; }

    // Intentionally lazy: shipping/runtime-only processes allocate no overlay GPU resources.
    public override void Initialize() { }

    public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData) => _source.Current is { IsEmpty: false };

    public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
    {
        OverlayDrawData? data = _source.Current;
        if (data == null || data.IsEmpty) return;
        EnsureInitialized();
        int frame = Math.Clamp(frameIndex, 0, _frames.Length - 1);
        EnsureCapacity(frame, data.Vertices.Length, data.Indices.Length);
        GpuBufferUploader.UploadSpanToBuffer(_context, _buffers, _staging, cmd, _frames[frame].Vertex, data.Vertices.AsSpan(),
            barrierDescription: new UploadBarrierDescription(PipelineStageFlags2.VertexAttributeInputBit, AccessFlags2.VertexAttributeReadBit));
        GpuBufferUploader.UploadSpanToBuffer(_context, _buffers, _staging, cmd, _frames[frame].Index, data.Indices.AsSpan(),
            barrierDescription: new UploadBarrierDescription(PipelineStageFlags2.IndexInputBit, AccessFlags2.IndexReadBit));

        uint imageIndex = sceneData.ImageIndex < _swapchain.ImageCount ? sceneData.ImageIndex : (uint)frame;
        RenderingAttachmentInfo color = ColorAttachment(_swapchain.ImageViews[imageIndex], ImageLayout.ColorAttachmentOptimal, AttachmentLoadOp.Load, AttachmentStoreOp.Store);
        var rendering = new RenderingInfo { SType = StructureType.RenderingInfo, RenderArea = new Rect2D(new Offset2D(0, 0), _swapchain.Extent), LayerCount = 1, ColorAttachmentCount = 1, PColorAttachments = &color };
        _context.KhrDynamicRendering.CmdBeginRendering(cmd, &rendering);
        _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline);
        BindBindlessStorageAndTextures(cmd, _layout);
        VkBuffer vertex = _buffers.GetBuffer(_frames[frame].Vertex), index = _buffers.GetBuffer(_frames[frame].Index);
        ulong zero = 0;
        _context.Api.CmdBindVertexBuffers(cmd, 0, 1, &vertex, &zero);
        _context.Api.CmdBindIndexBuffer(cmd, index, 0, IndexType.Uint16);
        var viewport = new Viewport { Width = _swapchain.Extent.Width, Height = _swapchain.Extent.Height, MaxDepth = 1f };
        _context.Api.CmdSetViewport(cmd, 0, 1, &viewport);

        foreach (OverlayDrawCommand command in data.Commands)
        {
            float l = (command.ClipRectangle.X - data.DisplayPosition.X) * data.FramebufferScale.X;
            float t = (command.ClipRectangle.Y - data.DisplayPosition.Y) * data.FramebufferScale.Y;
            float r = (command.ClipRectangle.Z - data.DisplayPosition.X) * data.FramebufferScale.X;
            float b = (command.ClipRectangle.W - data.DisplayPosition.Y) * data.FramebufferScale.Y;
            int x = Math.Max(0, (int)l), y = Math.Max(0, (int)t);
            int maxX = Math.Min((int)_swapchain.Extent.Width, (int)MathF.Ceiling(r));
            int maxY = Math.Min((int)_swapchain.Extent.Height, (int)MathF.Ceiling(b));
            if (maxX <= x || maxY <= y || command.ElementCount == 0) continue;
            var scissor = new Rect2D(new Offset2D(x, y), new Extent2D((uint)(maxX - x), (uint)(maxY - y)));
            _context.Api.CmdSetScissor(cmd, 0, 1, &scissor);
            var push = new Push { DisplayPosition = data.DisplayPosition, DisplaySize = data.DisplaySize, TextureIndex = (uint)Math.Max(command.TextureIndex, BindlessIndex.DefaultWhiteTexture) };
            _context.Api.CmdPushConstants(cmd, _layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)Marshal.SizeOf<Push>(), &push);
            _context.Api.CmdDrawIndexed(cmd, command.ElementCount, 1, command.IndexOffset, command.VertexOffset, 0);
        }
        _context.KhrDynamicRendering.CmdEndRendering(cmd);
    }

    public override void OnSwapchainRecreated() { if (_initialized) { DestroyPipeline(); _pipeline = CreatePipeline(); } }
    public override void Cleanup()
    {
        DestroyPipeline();
        if (_layout.Handle != 0)
        {
            _context.Api.DestroyPipelineLayout(_context.Device, _layout, null);
            _layout = default;
        }
        if (_cache.Handle != 0)
        {
            _context.Api.DestroyPipelineCache(_context.Device, _cache, null);
            _cache = default;
        }
        for (int i = 0; i < _frames.Length; i++)
        {
            FrameBuffers frame = _frames[i];
            if (frame.Vertex.IsValid) _buffers.DestroyBuffer(frame.Vertex);
            if (frame.Index.IsValid) _buffers.DestroyBuffer(frame.Index);
            _frames[i] = default;
        }
        if (_entry != 0)
        {
            SilkMarshal.Free(_entry);
            _entry = 0;
        }
        _initialized = false;
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;
        _entry = SilkMarshal.StringToPtr("main");
        _cache = GraphicsPipelineFactory.CreatePipelineCache(_context, "ImGui Pipeline Cache");
        var range = new PushConstantRange { StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, Size = (uint)Marshal.SizeOf<Push>() };
        _layout = GraphicsPipelineFactory.CreateBindlessPipelineLayout(_context, _bindlessHeap, range, "ImGui Pipeline Layout");
        _pipeline = CreatePipeline();
        for (int i = 0; i < _frames.Length; i++) _frames[i] = CreateBuffers(4096, 8192, i);
        _initialized = true;
    }

    private void EnsureCapacity(int frame, int vertices, int indices)
    {
        FrameBuffers old = _frames[frame];
        if (vertices <= old.VertexCapacity && indices <= old.IndexCapacity) return;
        if (old.Vertex.IsValid) _buffers.DestroyBuffer(old.Vertex); if (old.Index.IsValid) _buffers.DestroyBuffer(old.Index);
        int vc = Grow(old.VertexCapacity, vertices, 4096), ic = Grow(old.IndexCapacity, indices, 8192);
        _frames[frame] = CreateBuffers(vc, ic, frame);
    }
    private static int Grow(int value, int required, int initial) { value = Math.Max(value, initial); while (value < required) value = checked(value * 2); return value; }
    private FrameBuffers CreateBuffers(int vc, int ic, int frame) => new(
        _buffers.CreateDeviceBuffer((ulong)(vc * Marshal.SizeOf<OverlayVertex>()), BufferUsageFlags.VertexBufferBit | BufferUsageFlags.TransferDstBit, false, MemoryBudgetCategory.DiagnosticsAndDebug, $"ImGui.Vertex.Frame{frame}"),
        _buffers.CreateDeviceBuffer((ulong)(ic * sizeof(ushort)), BufferUsageFlags.IndexBufferBit | BufferUsageFlags.TransferDstBit, false, MemoryBudgetCategory.DiagnosticsAndDebug, $"ImGui.Index.Frame{frame}"), vc, ic);

    private VkPipeline CreatePipeline()
    {
        ShaderModule vs = default, fs = default;
        try
        {
            vs = ShaderModuleLoader.Load(_context, "imgui.vert.spv"); fs = ShaderModuleLoader.Load(_context, "imgui.frag.spv");
            var stages = stackalloc PipelineShaderStageCreateInfo[2] { GraphicsPipelineFactory.ShaderStage(ShaderStageFlags.VertexBit, vs, _entry), GraphicsPipelineFactory.ShaderStage(ShaderStageFlags.FragmentBit, fs, _entry) };
            var binding = new VertexInputBindingDescription { Stride = (uint)Marshal.SizeOf<OverlayVertex>(), InputRate = VertexInputRate.Vertex };
            var attrs = stackalloc VertexInputAttributeDescription[3] { new() { Location = 0, Format = Format.R32G32Sfloat }, new() { Location = 1, Format = Format.R32G32Sfloat, Offset = 8 }, new() { Location = 2, Format = Format.R8G8B8A8Unorm, Offset = 16 } };
            var input = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo, VertexBindingDescriptionCount = 1, PVertexBindingDescriptions = &binding, VertexAttributeDescriptionCount = 3, PVertexAttributeDescriptions = attrs };
            var assembly = GraphicsPipelineFactory.TriangleListInputAssembly(); var viewport = GraphicsPipelineFactory.DynamicViewportScissorState();
            var raster = GraphicsPipelineFactory.FillNoCullRasterization(); var multisample = GraphicsPipelineFactory.SingleSample();
            var depth = new PipelineDepthStencilStateCreateInfo { SType = StructureType.PipelineDepthStencilStateCreateInfo };
            var attachment = new PipelineColorBlendAttachmentState { BlendEnable = true, SrcColorBlendFactor = BlendFactor.SrcAlpha, DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha, ColorBlendOp = BlendOp.Add, SrcAlphaBlendFactor = BlendFactor.One, DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha, AlphaBlendOp = BlendOp.Add, ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit };
            var blend = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &attachment };
            var states = stackalloc DynamicState[2]; var dynamic = GraphicsPipelineFactory.DynamicViewportScissor(states);
            Format format = _swapchain.SurfaceFormat; var rendering = new PipelineRenderingCreateInfo { SType = StructureType.PipelineRenderingCreateInfo, ColorAttachmentCount = 1, PColorAttachmentFormats = &format };
            var info = new GraphicsPipelineCreateInfo { SType = StructureType.GraphicsPipelineCreateInfo, PNext = &rendering, StageCount = 2, PStages = stages, PVertexInputState = &input, PInputAssemblyState = &assembly, PViewportState = &viewport, PRasterizationState = &raster, PMultisampleState = &multisample, PDepthStencilState = &depth, PColorBlendState = &blend, PDynamicState = &dynamic, Layout = _layout };
            Result result = _context.Api.CreateGraphicsPipelines(_context.Device, _cache, 1, &info, null, out VkPipeline pipeline);
            if (result != Result.Success) throw new VulkanException("Failed to create ImGui pipeline", result);
            return pipeline;
        }
        finally { if (vs.Handle != 0) _context.Api.DestroyShaderModule(_context.Device, vs, null); if (fs.Handle != 0) _context.Api.DestroyShaderModule(_context.Device, fs, null); }
    }
    private void DestroyPipeline() { if (_pipeline.Handle != 0) _context.Api.DestroyPipeline(_context.Device, _pipeline, null); _pipeline = default; }
    [StructLayout(LayoutKind.Sequential, Pack = 4)] private struct Push { public Vector2 DisplayPosition; public Vector2 DisplaySize; public uint TextureIndex; }
    private readonly record struct FrameBuffers(BufferHandle Vertex, BufferHandle Index, int VertexCapacity, int IndexCapacity);
}
