using Njulf.Core.Math;
using Njulf.Graphics;
using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;
using Silk.NET.Vulkan;

namespace Njulf.Rendering;

public partial class VulkanRenderer
{
    private ImGuiRenderPass? _terminalOverlayPass;
    private OverlayDrawData? _editorOverlayDrawData;
    private readonly OverlayFrameBuilder _spriteOverlayData = new();
    private readonly SceneRenderingData _overlaySceneData = new();
    private Color _frameClearColor = Color.Black;
    internal Vector2 SpriteDisplaySize { get; private set; }
    internal ulong SpriteFrameSerial { get; private set; }

    /// <summary>Sets logical window size independently of framebuffer and scene resolution.</summary>
    public void SetSpriteDisplaySize(Vector2 size)
    {
        if (!float.IsFinite(size.X) || !float.IsFinite(size.Y) || size.X <= 0 || size.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(size));
        SpriteDisplaySize = size;
    }

    internal void QueueSpriteDrawData(SpriteDrawData data)
    {
        _spriteOverlayData.Append(data, _swapchain.Extent.Width, _swapchain.Extent.Height);
    }

    private unsafe void RecordTerminalOverlays()
    {
        if (_spriteOverlayData.Count == 0 && _editorOverlayDrawData is not { IsEmpty: false }) { ClearOverlaySubmissions(); return; }
        if (!_swapchainImageTransitionedThisFrame) RecordProgressiveClear(_frameClearColor);
        // Scene/custom effects and loading clears may have just written this attachment.
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
            SrcAccessMask = AccessFlags2.ColorAttachmentWriteBit,
            DstStageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
            DstAccessMask = AccessFlags2.ColorAttachmentReadBit | AccessFlags2.ColorAttachmentWriteBit
        };
        var dependency = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &barrier };
        _context.Api.CmdPipelineBarrier2(_currentCommandBuffer, &dependency);
        _terminalOverlayPass ??= new(_context, _swapchain, _bindlessHeap, _bufferManager, _stagingRing, _overlayDrawData);
        if (_editorOverlayDrawData is { IsEmpty: false }) _spriteOverlayData.Append(_editorOverlayDrawData);
        _overlayDrawData.Set(_spriteOverlayData.Build(_swapchain.Extent.Width, _swapchain.Extent.Height));
        _overlaySceneData.ImageIndex = _imageIndex;
        try { _terminalOverlayPass.Execute(_currentCommandBuffer, _currentFrame, _overlaySceneData); }
        finally { ClearOverlaySubmissions(); }
    }

    private void ClearOverlaySubmissions()
    {
        _spriteOverlayData.Clear(); _editorOverlayDrawData = null; _overlayDrawData.Set(null);
    }
}
