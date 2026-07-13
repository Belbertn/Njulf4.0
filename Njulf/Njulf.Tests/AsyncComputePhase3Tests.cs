using System.IO;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class AsyncComputePhase3Tests
{
    [Test]
    public void VulkanContext_ExposesRealDedicatedComputeQueueSupport()
    {
        string context = ReadRepoText("Njulf.Rendering", "Core", "VulkanContext.cs");
        string commands = ReadRepoText("Njulf.Rendering", "Core", "CommandBufferManager.cs");

        Assert.That(context, Does.Contain("public uint ComputeQueueFamilyIndex"));
        Assert.That(context, Does.Contain("public Queue ComputeQueue"));
        Assert.That(context, Does.Contain("public bool HasDedicatedComputeQueue"));
        Assert.That(context, Does.Contain("QueueFlags.ComputeBit"));
        Assert.That(context, Does.Contain("_vk.GetDeviceQueue(_device, _computeQueueFamilyIndex, 0, out _computeQueue);"));
        Assert.That(commands, Does.Contain("BeginAsyncComputeCommand"));
        Assert.That(commands, Does.Contain("SemaphoreType = SemaphoreType.Timeline"));
        Assert.That(commands, Does.Contain("public Semaphore AsyncComputeTimelineSemaphore"));

        string timestamps = ReadRepoText("Njulf.Rendering", "Debugging", "GpuTimestampRecorder.cs");
        Assert.That(timestamps, Does.Contain("_graphicsQueryPools"));
        Assert.That(timestamps, Does.Contain("_computeQueryPools"));
        Assert.That(timestamps, Does.Contain("BeginComputePass"));
    }

    [Test]
    public void SimpleDdgiAsyncPath_TransfersEveryAsyncOwnedBufferBackToGraphicsBeforeLateGraph()
    {
        string manager = ReadRepoText("Njulf.Rendering", "Resources", "SimpleDdgiVolumeManager.cs");
        string renderer = ReadRepoText("Njulf.Rendering", "VulkanRenderer.cs");

        string[] buffers =
        [
            "_paramsBuffer",
            "_irradianceAtlasBuffer",
            "_visibilityAtlasBuffer",
            "_rayResultScratchBuffer",
            "_probeStateBuffer",
            "_probeUpdateQueueBuffer",
            "_relocationClassificationBuffer"
        ];
        foreach (string buffer in buffers)
            Assert.That(manager, Does.Contain($"AddQueueTransferBarrier(barriers, ref barrierCount, {buffer}"));

        Assert.That(renderer, Does.Contain("SimpleDdgiQueueTransfer.GraphicsReleaseToCompute"));
        Assert.That(renderer, Does.Contain("SimpleDdgiQueueTransfer.ComputeAcquireFromGraphics"));
        Assert.That(renderer, Does.Contain("SimpleDdgiQueueTransfer.ComputeReleaseToGraphics"));
        Assert.That(renderer, Does.Contain("SimpleDdgiQueueTransfer.GraphicsAcquireFromCompute"));
        Assert.That(renderer.IndexOf("SimpleDdgiQueueTransfer.GraphicsAcquireFromCompute"),
            Is.LessThan(renderer.IndexOf("static passName => !IsSceneSubmissionPass(passName)")));
    }

    [Test]
    public void Renderer_SplitsGraphAroundSceneSubmissionAndForwardForSimpleDdgiOverlap()
    {
        string renderer = ReadRepoText("Njulf.Rendering", "VulkanRenderer.cs");

        Assert.That(renderer, Does.Contain("ExecuteRenderGraphWithAsyncSimpleDdgi"));
        Assert.That(renderer, Does.Contain("static passName => passName == \"SceneOpaqueCompactionPass\""));
        Assert.That(renderer, Does.Contain("SubmitAsyncComputeCommand(asyncCompute, setupValue, computeCompleteValue);"));
        Assert.That(renderer, Does.Contain("SubmitAsyncEarlyGraphicsCommand(earlyGraphics);"));
        Assert.That(renderer, Does.Contain("PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit"));
        Assert.That(renderer, Does.Contain("\"ForwardPlusPass\""));
        Assert.That(renderer, Does.Contain("\"SimpleDdgiTracePass\" or \"SimpleDdgiRelocateClassifyPass\" or \"SimpleDdgiBlendPass\""));
    }

    [Test]
    public void AsyncComputePlan_RuntimeFlipFallsBackWhenDisabledOrUnsupported()
    {
        string renderer = ReadRepoText("Njulf.Rendering", "VulkanRenderer.cs");

        Assert.That(renderer, Does.Contain("bool requested = Settings.AsyncCompute.Enabled;"));
        Assert.That(renderer, Does.Contain("bool supported = _context.HasDedicatedComputeQueue && _cmd.AsyncComputeTimelineSemaphore.Handle != 0;"));
        Assert.That(renderer, Does.Contain("if (!requested)"));
        Assert.That(renderer, Does.Contain("else if (!supported)"));
        Assert.That(renderer, Does.Contain("sceneData.DdgiAsyncComputeEnabled = 0;"));
        Assert.That(renderer, Does.Contain("sceneData.DdgiAsyncComputeEnabled = 1;"));
    }

    private static string ReadRepoText(params string[] pathParts)
    {
        string baseDir = TestContext.CurrentContext.TestDirectory;
        DirectoryInfo? dir = new(baseDir);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Njulf.sln")))
            dir = dir.Parent;

        Assert.That(dir, Is.Not.Null, "Could not locate repository root.");
        return File.ReadAllText(Path.Combine([dir!.FullName, .. pathParts]));
    }
}
