using Njulf.Rendering.Pipeline;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed class QueueStageCapabilitiesTests
{
    [Test]
    public void ComputeOnlyQueueRejectsGraphicsStagesAndAttachmentAccess()
    {
        var capabilities = new QueueStageCapabilities(QueueFlags.ComputeBit | QueueFlags.TransferBit);
        Assert.Multiple(() =>
        {
            Assert.That(capabilities.SupportsScope(PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit), Is.True);
            Assert.That(capabilities.SupportsScope(
                PipelineStageFlags2.DrawIndirectBit,
                AccessFlags2.IndirectCommandReadBit), Is.True);
            Assert.That(capabilities.SupportsStages(PipelineStageFlags2.FragmentShaderBit), Is.False);
            Assert.That(capabilities.SupportsScope(PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ColorAttachmentWriteBit), Is.False);
            Assert.That(capabilities.SupportsScope(PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryReadBit), Is.True);
        });
    }

    [Test]
    public void IgnoredTransferScopeRequiresBothMasksToBeNone()
    {
        var capabilities = new QueueStageCapabilities(QueueFlags.ComputeBit);
        Assert.That(capabilities.SupportsScope(PipelineStageFlags2.None, AccessFlags2.None, ignoredScope: true), Is.True);
        Assert.That(capabilities.SupportsScope(PipelineStageFlags2.ComputeShaderBit, AccessFlags2.None, ignoredScope: true), Is.False);
    }
}
