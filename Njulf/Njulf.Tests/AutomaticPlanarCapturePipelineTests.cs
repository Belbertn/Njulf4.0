using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline.PipelineObjects;
using NUnit.Framework;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Tests;

[TestFixture]
public sealed class AutomaticPlanarCapturePipelineTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void Buckets_PreserveMaterialInterfaceAndSubmissionStage(bool taskless)
    {
        Assert.Multiple(() =>
        {
            Assert.That(AutomaticPlanarCapturePipelineBank.ResolveFamily(MaterialForwardClass.SimpleOpaque, true, taskless),
                Is.EqualTo(taskless ? ForwardOpaquePipelineFamily.CompactedSimple : ForwardOpaquePipelineFamily.Simple));
            Assert.That(AutomaticPlanarCapturePipelineBank.ResolveFamily(MaterialForwardClass.SimpleOpaqueNormal, true, taskless),
                Is.EqualTo(taskless ? ForwardOpaquePipelineFamily.CompactedSimpleFullInput : ForwardOpaquePipelineFamily.SimpleFullInput));
            Assert.That(AutomaticPlanarCapturePipelineBank.ResolveFamily(MaterialForwardClass.FullOpaque, true, taskless),
                Is.EqualTo(taskless ? ForwardOpaquePipelineFamily.CompactedFull : ForwardOpaquePipelineFamily.Full));
            Assert.That(AutomaticPlanarCapturePipelineBank.ResolveFamily(MaterialForwardClass.SimpleOpaque, false, taskless),
                Is.EqualTo(taskless ? ForwardOpaquePipelineFamily.CompactedFull : ForwardOpaquePipelineFamily.Full));
        });
    }

    [TestCase(ForwardOpaquePipelineFamily.Simple)]
    [TestCase(ForwardOpaquePipelineFamily.SimpleFullInput)]
    [TestCase(ForwardOpaquePipelineFamily.CompactedSimple)]
    [TestCase(ForwardOpaquePipelineFamily.CompactedSimpleFullInput)]
    public void LateFamilyPublication_ReplacesTemporaryFullFallback(ForwardOpaquePipelineFamily family)
    {
        var bank = new AutomaticPlanarCapturePipelineBank();
        bank.Publish(ForwardOpaquePipelineFamily.Full, new VkPipeline(10), new VkPipeline(11), true);
        var color = new ForwardOpaquePipelineKey(family, ForwardOpaquePipelineFeatures.None);
        var feedback = color with { Features = ForwardOpaquePipelineFeatures.AlphaMaskReceiverFeedback };
        Assert.That(bank.TryResolve(color, out VkPipeline fallback), Is.True);
        Assert.That(fallback.Handle, Is.EqualTo(10));
        Assert.Throws<InvalidOperationException>(() => bank.Publish(family, new VkPipeline(20), default, true));
        Assert.That(bank.TryResolve(feedback, out fallback), Is.True);
        Assert.That(fallback.Handle, Is.EqualTo(11));
        bank.Publish(family, new VkPipeline(20), new VkPipeline(21), true);
        Assert.That(bank.TryResolve(color, out VkPipeline selected), Is.True);
        Assert.That(selected.Handle, Is.EqualTo(20));
        Assert.That(bank.TryResolve(feedback, out selected), Is.True);
        Assert.That(selected.Handle, Is.EqualTo(21));
        bank.Clear();
        Assert.That(bank.TryResolve(color, out _), Is.False);
    }
}
