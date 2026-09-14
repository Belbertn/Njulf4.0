using System;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using Vma;
using VkPipeline = Silk.NET.Vulkan.Pipeline;
namespace Njulf.Rendering.Pipeline;

internal sealed unsafe partial class HybridReflectionVulkanRuntime
{
    private readonly BufferHandle[] _amdStates = [BufferHandle.Invalid, BufferHandle.Invalid];
    private readonly VkPipeline[] _amdPipelines = new VkPipeline[5];
    private bool _amdActive, _amdHistory;
    private Matrix4x4 _amdPreviousVp = Matrix4x4.Identity;
    private ReflectionDenoiser _previousDenoiser;
    private bool _amdFailed;
    private void PrepareAmdResources()
    {
        if (_amdFailed) { _amdActive = false; return; }
        try { PrepareAmdResourcesCore(); }
        catch (Exception exception)
        {
            _amdFailed = true; _amdActive = false; _amdHistory = false;
            Console.Error.WriteLine($"AMD reflection denoising unavailable; using existing filter: {exception.Message}");
        }
    }
    private void PrepareAmdResourcesCore()
    {
        if (_previousDenoiser != _settings.Reflections.Denoiser)
        {
            InvalidateHistory(); _amdHistory = false;
            _previousDenoiser = _settings.Reflections.Denoiser;
        }
        _amdActive = _settings.Reflections.Denoiser == ReflectionDenoiser.Amd;
        if (!_amdActive && _settings.Reflections.Denoiser != ReflectionDenoiser.Off) return;
        if (_amdPipelines[0].Handle == 0)
        {
            string[] names = ["reproject", "prefilter", "temporal", "copy", "prepare"];
            for (int i = 0; i < names.Length; i++)
                _amdPipelines[i] = CreatePipeline($"amd_reflection_{names[i]}{(i < 3 && _context.ShaderFloat16Enabled ? "_half" : "")}.comp.spv");
        }
        if (!_amdActive || _amdStates[0].IsValid) return;
        ulong bytes = checked((128UL + (ulong)_allocatedWidth * _allocatedHeight * 16UL +
            (ulong)((_allocatedWidth + 7) / 8) * ((_allocatedHeight + 7) / 8) * 2UL) * 4UL);
        if (bytes > _context.MaximumStorageBufferRange)
            throw new InvalidOperationException("AMD reflection guides exceed maxStorageBufferRange.");
        for (int bank = 0; bank < 2; bank++)
        {
            _amdStates[bank] = _bufferManager.CreateBuffer(bytes,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                MemoryUsage.AutoPreferDevice, debugName: $"AMD reflection guides {bank}",
                category: MemoryBudgetCategory.RenderTargets);
            _bindlessHeap.RegisterStorageBuffer(BindlessIndex.AmdReflectionBufferBase + bank,
                _bufferManager.GetBuffer(_amdStates[bank]), 0, bytes);
        }
        _amdHistory = false;
    }
    private void AmdBarrier(CommandBuffer cmd)
    {
        var memory = new MemoryBarrier2 { SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.AllCommandsBit,
            SrcAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
            DstStageMask = PipelineStageFlags2.AllCommandsBit,
            DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit };
        var dependency = new DependencyInfo { SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1, PMemoryBarriers = &memory };
        _context.Api.CmdPipelineBarrier2(cmd, &dependency);
    }
    private void BeginAmdFrame(CommandBuffer cmd, int bank)
    {
        if (!_amdActive) return;
        AmdBarrier(cmd);
        _context.Api.CmdFillBuffer(cmd, _bufferManager.GetBuffer(_amdStates[bank]), 0,
            _bufferManager.GetBufferSize(_amdStates[bank]), 0);
        AmdBarrier(cmd);
    }
    private bool RecordAmdTemporal(CommandBuffer cmd, int bank, SceneRenderingData scene)
    {
        bool off = _settings.Reflections.Denoiser == ReflectionDenoiser.Off;
        if ((!_amdActive && !off) || _amdFailed) return false;
        uint* push = stackalloc uint[6];
        push[0] = (uint)(BindlessIndex.AmdReflectionBufferBase + bank);
        push[1] = (uint)(BindlessIndex.AmdReflectionBufferBase + 1 - bank);
        push[2] = _allocatedWidth; push[3] = _allocatedHeight;
        push[4] = !_amdHistory || _currentResetReasons != ReflectionHistoryResetReason.None ||
            _currentSourceInvalidations != 0 ? 1u : 0u;
        if (!off)
        {
            uint* header = stackalloc uint[48];
            *(Matrix4x4*)header = scene.InverseProjectionMatrix;
            *(Matrix4x4*)(header + 16) = scene.InverseViewMatrix;
            *(Matrix4x4*)(header + 32) = _amdPreviousVp;
            AmdBarrier(cmd);
            _context.Api.CmdUpdateBuffer(cmd, _bufferManager.GetBuffer(_amdStates[bank]), 0, 192, header);
            AmdBarrier(cmd);
            BindPipelineAndDescriptors(cmd, _amdPipelines[4], bank, bindRayScene: false);
            push[5] = 0;
            _context.Api.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.ComputeBit, 0, 24, push);
            _context.Api.CmdDispatch(cmd, (_allocatedWidth + 7) / 8, (_allocatedHeight + 7) / 8, 1);
            AmdBarrier(cmd);
        }
        for (int i = off ? 3 : 0; i < (off ? 4 : 3); i++)
        {
            BindPipelineAndDescriptors(cmd, _amdPipelines[i], bank, bindRayScene: false);
            push[5] = (uint)i + 1;
            _context.Api.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.ComputeBit, 0, 24, push);
            _context.Api.CmdDispatch(cmd, (_allocatedWidth + 7) / 8, (_allocatedHeight + 7) / 8, 1);
            AmdBarrier(cmd);
        }
        _amdPreviousVp = scene.ViewProjectionMatrix; _amdHistory = !off;
        return true;
    }
}
