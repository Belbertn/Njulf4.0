using Njulf.Rendering.Core;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public class SwapchainExtentTests
{
    [Test]
    public void ChooseSwapExtent_ReturnsZeroWhileVariableExtentFramebufferIsMinimized()
    {
        SurfaceCapabilitiesKHR capabilities = CreateVariableCapabilities();

        Extent2D extent = SwapchainManager.ChooseSwapExtent(0, 0, capabilities);

        Assert.That(extent.Width, Is.Zero);
        Assert.That(extent.Height, Is.Zero);
    }

    [Test]
    public void ChooseSwapExtent_UsesAndClampsFramebufferPixelSize()
    {
        SurfaceCapabilitiesKHR capabilities = CreateVariableCapabilities();

        Extent2D extent = SwapchainManager.ChooseSwapExtent(5000, 720, capabilities);

        Assert.That(extent.Width, Is.EqualTo(4096));
        Assert.That(extent.Height, Is.EqualTo(720));
    }

    [Test]
    public void ChooseSwapExtent_PreservesSurfaceDefinedExtent()
    {
        SurfaceCapabilitiesKHR capabilities = CreateVariableCapabilities();
        capabilities.CurrentExtent = new Extent2D { Width = 1920, Height = 1080 };

        Extent2D extent = SwapchainManager.ChooseSwapExtent(0, 0, capabilities);

        Assert.That(extent.Width, Is.EqualTo(1920));
        Assert.That(extent.Height, Is.EqualTo(1080));
    }

    private static SurfaceCapabilitiesKHR CreateVariableCapabilities()
    {
        return new SurfaceCapabilitiesKHR
        {
            CurrentExtent = new Extent2D { Width = uint.MaxValue, Height = uint.MaxValue },
            MinImageExtent = new Extent2D { Width = 1, Height = 1 },
            MaxImageExtent = new Extent2D { Width = 4096, Height = 4096 }
        };
    }
}
