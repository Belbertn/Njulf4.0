using System;
using Njulf.Rendering.Core;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline;

/// <summary>
/// Executes the exact authored-instance expansion list emitted by foliage
/// view culling. One indirect compute workgroup owns one instance command;
/// its 64 lanes append only complete selected-LOD meshlet commands.
/// </summary>
public sealed unsafe class FoliageAuthoredExpandPass
{
    private readonly VulkanContext _context;
    private readonly BufferManager _bufferManager;
    private readonly FoliagePipeline _pipeline;

    public FoliageAuthoredExpandPass(
        VulkanContext context,
        BufferManager bufferManager,
        FoliagePipeline pipeline)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _bufferManager = bufferManager ??
            throw new ArgumentNullException(nameof(bufferManager));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
    }

    public void Execute(
        CommandBuffer commandBuffer,
        in FoliageRuntimeBuffers buffers)
    {
        if (!buffers.IndirectDispatchBuffer.IsValid ||
            _pipeline.AuthoredExpandPipeline.Handle == 0)
        {
            return;
        }
        _context.Api.CmdBindPipeline(
            commandBuffer,
            PipelineBindPoint.Compute,
            _pipeline.AuthoredExpandPipeline);
        Silk.NET.Vulkan.Buffer indirect = _bufferManager.GetBuffer(
            buffers.IndirectDispatchBuffer);
        _context.Api.CmdDispatchIndirect(
            commandBuffer,
            indirect,
            FoliageManager.AuthoredExpandIndirectDispatchOffset);
    }
}
