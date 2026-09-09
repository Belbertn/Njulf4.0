using Njulf.Rendering.Core;
using Njulf.Rendering.Pipeline;
using Silk.NET.Vulkan;

namespace Njulf.Graphics.Vulkan;

// Borrowed image state shared by every descriptor alias. TextureManager owns allocation lifetime.
internal sealed class TextureGraphImage : IRenderGraphLayoutTrackedImage
{
    private static long _nextGeneration;
    internal ulong AllocationGeneration { get; } = (ulong)Interlocked.Increment(ref _nextGeneration);
    private readonly VulkanContext _context;
    internal VulkanPassImage Image { get; }
    internal ImageSubresourceRange Range { get; }
    internal bool Writable { get; }
    internal Action<ImageLayout> LayoutTracker { get; }
    internal Func<ImageLayout> LayoutProvider { get; }
    public ImageLayout Layout { get; internal set; }
    internal TextureGraphImage(VulkanContext context, VulkanPassImage image, uint mipLevels, uint layers, bool writable)
    {
        _context = context; Image = image; Writable = writable;
        LayoutTracker = SetLayout;
        LayoutProvider = GetLayout;
        Range = new(ImageAspectFlags.ColorBit, 0, mipLevels, 0, layers);
        Layout = writable ? ImageLayout.Undefined : ImageLayout.ShaderReadOnlyOptimal;
    }
    private void SetLayout(ImageLayout layout) => Layout = layout;
    private ImageLayout GetLayout() => Layout;
    public unsafe void TransitionToLayout(CommandBuffer cmd, ImageLayout newLayout,
        PipelineStageFlags2 dstStage, AccessFlags2 dstAccess,
        PipelineStageFlags2? srcStage = null, AccessFlags2? srcAccess = null, bool force = false)
    {
        if (!force && Layout == newLayout) return;
        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = Layout == ImageLayout.Undefined ? PipelineStageFlags2.None : srcStage ?? PipelineStageFlags2.AllCommandsBit,
            SrcAccessMask = Layout == ImageLayout.Undefined ? AccessFlags2.None : srcAccess ?? (AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit),
            DstStageMask = dstStage, DstAccessMask = dstAccess, OldLayout = Layout, NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = Image.Image, SubresourceRange = Range
        };
        var dependency = new DependencyInfo { SType = StructureType.DependencyInfo, ImageMemoryBarrierCount = 1, PImageMemoryBarriers = &barrier };
        _context.Api.CmdPipelineBarrier2(cmd, &dependency);
        Layout = newLayout;
    }
}
