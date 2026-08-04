using System.IO;
using System.Linq;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Data;
using Silk.NET.Vulkan;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class ProductionRenderPipelineDeclarationTests
{
    [Test]
    public void ProductionDeclaration_ContainsSimpleDdgi()
    {
        string source = ReadRepoText("Njulf.Rendering", "Pipeline", "ProductionRenderPipelineDeclaration.cs");

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("SimpleDdgi"));
        });
    }

    [Test]
    public void SimpleDdgiIndirectConsumersDeclareComputeAndDrawIndirectSchedulerAccess()
    {
        string[] consumerNames =
        [
            "SimpleDdgiTracePass",
            "SimpleDdgiRelocateClassifyPass",
            "SimpleDdgiTransportPass",
            "SimpleDdgiBlendPass",
            "SimpleDdgiPublishPass",
            "SimpleDdgiSchedulerCommitPass"
        ];
        var declarations = ProductionRenderPipelineDeclaration.Instance
            .CreatePassResourceDeclarations()
            .ToDictionary(declaration => declaration.PassName);
        PipelineStageFlags2 requiredStages =
            PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.DrawIndirectBit;
        AccessFlags2 requiredAccess =
            AccessFlags2.ShaderStorageReadBit |
            AccessFlags2.ShaderStorageWriteBit |
            AccessFlags2.IndirectCommandReadBit;

        foreach (string name in consumerNames)
        {
            RenderGraphResourceUsage usage = declarations[name].Usages.Single(
                usage => usage.Resource == RenderGraphResourceId.SimpleDdgiScheduler);
            Assert.Multiple(() =>
            {
                Assert.That(usage.Access, Is.EqualTo(RenderGraphResourceAccess.ReadWrite), name);
                Assert.That(usage.StageMask & requiredStages, Is.EqualTo(requiredStages), name);
                Assert.That(usage.AccessMask & requiredAccess, Is.EqualTo(requiredAccess), name);
            });
        }
    }

    [Test]
    public void ScheduleAndCommitRecordArenaWideComputeToIndirectBufferBarriers()
    {
        string schedule = ReadRepoText(
            "Njulf.Rendering", "Pipeline", "SimpleDdgiSchedulePass.cs");
        string commit = ReadRepoText(
            "Njulf.Rendering", "Pipeline", "SimpleDdgiSchedulerCommitPass.cs");

        Assert.Multiple(() =>
        {
            Assert.That(schedule, Does.Contain("BufferMemoryBarrier2"));
            Assert.That(schedule, Does.Contain("DrawIndirectBit"));
            Assert.That(schedule, Does.Contain("IndirectCommandReadBit"));
            Assert.That(schedule, Does.Contain("GetArenaVkBuffer()"));
            Assert.That(schedule, Does.Contain("GetProbeUpdateQueueVkBuffer()"));
            Assert.That(schedule, Does.Contain("ProbeUpdateQueueBytes"));
            Assert.That(schedule, Does.Contain("Vk.QueueFamilyIgnored"));
            Assert.That(commit, Does.Contain("BufferMemoryBarrier2"));
            Assert.That(commit, Does.Contain("DrawIndirectBit"));
            Assert.That(commit, Does.Contain("IndirectCommandReadBit"));
            Assert.That(commit, Does.Contain("GetArenaVkBuffer()"));
            Assert.That(commit, Does.Contain("Vk.QueueFamilyIgnored"));
        });
    }

    private static string ReadRepoText(params string[] segments)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, segments));
    }
}
