using Silk.NET.Vulkan;

namespace Njulf.Rendering.Core
{
    /// <summary>
    /// Describes whether a presentation surface permits a swapchain image to be
    /// copied into a renderer-owned readback buffer.  The surface capability is
    /// intentionally kept separate from pixel-format support: a surface can
    /// allow transfer reads while still exposing a format the PNG capture path
    /// deliberately rejects.
    /// </summary>
    public readonly record struct SwapchainScreenshotCapability(bool TransferSourceSupported, string Reason)
    {
        public static SwapchainScreenshotCapability Evaluate(ImageUsageFlags supportedUsageFlags)
        {
            bool supported = (supportedUsageFlags & ImageUsageFlags.TransferSrcBit) != 0;
            return supported
                ? new SwapchainScreenshotCapability(
                    true,
                    "The presentation surface supports TransferSrc usage for swapchain images.")
                : new SwapchainScreenshotCapability(
                    false,
                    "The presentation surface does not support TransferSrc usage for swapchain images; renderer PNG capture is unavailable.");
        }
    }
}
