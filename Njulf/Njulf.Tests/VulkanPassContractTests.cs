using Njulf.Graphics.Vulkan;
using Njulf.Rendering.Pipeline;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed class VulkanPassContractTests
{
    [TestCase(VulkanPassStage.BeforeScene, "SceneOpaqueCompactionPass")]
    [TestCase(VulkanPassStage.AfterScene, "FogPass")]
    [TestCase(VulkanPassStage.AfterPostProcessing, "ImGuiRenderPass")]
    public void StagesUseProductionAnchors(VulkanPassStage stage, string anchor)
        => Assert.That(VulkanPassRegistry.Anchor(stage), Is.EqualTo(anchor));

    [Test]
    public void BackbufferIsOnlyAvailableAsFinalColorAttachment()
    {
        var use = new VulkanImageUse("backbuffer", RenderGraphResourceAccess.Write,
            PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit,
            ImageLayout.ColorAttachmentOptimal, ViewImage: VulkanViewImage.Backbuffer);
        Assert.That(() => VulkanPassRegistry.ValidateImage(VulkanPassStage.AfterPostProcessing, use), Throws.Nothing);
        Assert.That(() => VulkanPassRegistry.ValidateImage(VulkanPassStage.AfterScene, use), Throws.ArgumentException);
        Assert.That(() => VulkanPassRegistry.ValidateImage(VulkanPassStage.AfterPostProcessing,
            use with { Layout = ImageLayout.General }), Throws.ArgumentException);
    }
    [Test]
    public void DepthWritesAndAttachmentSamplingAreRejected()
    {
        var depth = new VulkanImageUse("depth", RenderGraphResourceAccess.Write,
            PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit,
            ImageLayout.DepthStencilReadOnlyOptimal, ViewImage: VulkanViewImage.SceneDepth);
        Assert.That(() => VulkanPassRegistry.ValidateImage(VulkanPassStage.AfterScene, depth), Throws.ArgumentException);
        var feedback = depth with { ViewImage = VulkanViewImage.SceneColor, Layout = ImageLayout.ColorAttachmentOptimal };
        Assert.That(() => VulkanPassRegistry.ValidateImage(VulkanPassStage.AfterScene, feedback), Throws.ArgumentException);
    }
}
