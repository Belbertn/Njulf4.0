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
    internal Njulf.Rendering.Debug.GpuTimestampRecorder? DenoisingTimestamps { get; set; }
    private readonly BufferHandle[] _amdStates = [BufferHandle.Invalid, BufferHandle.Invalid];
    private readonly BufferHandle[] _amdReadback = [BufferHandle.Invalid, BufferHandle.Invalid];
    private readonly bool[] _amdSubmitted = new bool[2];
    private uint _amdLastDispatchedPixels;
    private readonly VkPipeline[] _amdPipelines = new VkPipeline[6];
    private bool _amdReduced;
    private uint _amdWidth, _amdHeight;
    private bool _amdActive, _amdHistory;
    private Matrix4x4 _amdPreviousVp = Matrix4x4.Identity;
    private bool _amdFailed;
    private ReflectionDenoiser _effectiveDenoiser;
    private void PrepareAmdResources()
    {
        bool reduced = _settings.Reflections.AmdHalfResolution;
        if (_amdStates[0].IsValid && _amdReduced != reduced)
        {
            Result idle = _context.Api.DeviceWaitIdle(_context.Device);
            if (idle != Result.Success)
                throw new InvalidOperationException($"AMD reflection resource replacement failed: {idle}");
            DestroyBufferArray(_amdStates);
            DestroyBufferArray(_amdReadback);
            Array.Clear(_amdSubmitted);
            _amdHistory = false;
            _amdFailed = false;
            InvalidateHistory();
        }
        _amdReduced = reduced;
        ReflectionDenoiser selected = _amdFailed && _settings.Reflections.Denoiser == ReflectionDenoiser.Amd
            ? ReflectionDenoiser.Existing : _settings.Reflections.Denoiser;
        if (_effectiveDenoiser != selected)
        {
            InvalidateHistory(); _amdHistory = false;
        }
        _effectiveDenoiser = selected;
        _amdActive = selected == ReflectionDenoiser.Amd;
        try { PrepareSelectedDenoiser(); }
        catch (Exception exception) when (_amdActive)
        {
            _amdFailed = true; _amdActive = false; _amdHistory = false;
            _effectiveDenoiser = ReflectionDenoiser.Existing;
            InvalidateHistory();
            Console.Error.WriteLine($"AMD reflection denoising unavailable; using existing filter: {exception.Message}");
            PrepareSelectedDenoiser();
        }
    }
    private void PrepareSelectedDenoiser()
    {
        if (_effectiveDenoiser == ReflectionDenoiser.Existing)
        {
            if (_temporalPipeline.Handle == 0) _temporalPipeline = CreatePipeline("hybrid_reflection_temporal.comp.spv");
            if (_spatialPipeline.Handle == 0) _spatialPipeline = CreatePipeline("hybrid_reflection_spatial.comp.spv");
            return;
        }
        if (_effectiveDenoiser == ReflectionDenoiser.Off)
        {
            if (_amdPipelines[3].Handle == 0) _amdPipelines[3] = CreatePipeline("amd_reflection_copy.comp.spv");
            return;
        }
        if (_amdPipelines[0].Handle == 0)
        {
            string[] names = ["reproject", "prefilter", "temporal", "copy", "prepare"];
            for (int i = 0; i < names.Length; i++)
                if (i != 3 && _amdPipelines[i].Handle == 0)
                    _amdPipelines[i] = CreatePipeline($"amd_reflection_{names[i]}{(i < 3 && _context.ShaderFloat16Enabled ? "_half" : "")}.comp.spv");
        }
        if (_amdReduced && _amdPipelines[5].Handle == 0)
            _amdPipelines[5] = CreatePipeline("amd_reflection_reconstruct.comp.spv");
        if (!_amdActive || _amdStates[0].IsValid) return;
        for (int bank = 0; bank < 2; bank++)
        {
            var allocation = AmdReflectionGpuContract.Layout(_allocatedWidth, _allocatedHeight, _amdReduced, bank);
            _amdWidth = allocation.Width; _amdHeight = allocation.Height;
            ulong bankBytes = allocation.Bytes;
            if (bankBytes > _context.MaximumStorageBufferRange)
                throw new InvalidOperationException("AMD reflection guides exceed maxStorageBufferRange.");
            _amdStates[bank] = _bufferManager.CreateBuffer(bankBytes,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit | BufferUsageFlags.IndirectBufferBit,
                MemoryUsage.AutoPreferDevice, debugName: $"AMD reflection guides {bank}",
                category: MemoryBudgetCategory.RenderTargets);
            _bindlessHeap.RegisterStorageBuffer(BindlessIndex.AmdReflectionBufferBase + bank,
                _bufferManager.GetBuffer(_amdStates[bank]), 0, bankBytes);
            if (_amdReduced)
                _amdReadback[bank] = _bufferManager.CreateBuffer(4, BufferUsageFlags.TransferDstBit,
                    MemoryUsage.AutoPreferHost, AllocationCreateFlags.MappedBit | AllocationCreateFlags.HostAccessRandomBit,
                    debugName: $"AMD reflection tile count {bank}", category: MemoryBudgetCategory.DiagnosticsAndDebug);
        }
        _amdHistory = false;
    }
    private void AmdBarrier(CommandBuffer cmd)
    {
        var memory = new MemoryBarrier2 { SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.TransferBit,
            SrcAccessMask = AccessFlags2.ShaderReadBit | AccessFlags2.ShaderWriteBit | AccessFlags2.TransferWriteBit | AccessFlags2.TransferReadBit,
            DstStageMask = PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.TransferBit | PipelineStageFlags2.DrawIndirectBit,
            DstAccessMask = AccessFlags2.ShaderReadBit | AccessFlags2.ShaderWriteBit | AccessFlags2.TransferWriteBit | AccessFlags2.TransferReadBit | AccessFlags2.IndirectCommandReadBit };
        var dependency = new DependencyInfo { SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1, PMemoryBarriers = &memory };
        _context.Api.CmdPipelineBarrier2(cmd, &dependency);
    }
    private void BeginAmdFrame(CommandBuffer cmd, int bank)
    {
        if (!_amdActive) return;
        if (_amdReduced && _amdSubmitted[bank])
        {
            _bufferManager.InvalidateBuffer(_amdReadback[bank], 0, 4);
            _amdLastDispatchedPixels = *(uint*)_bufferManager.GetMappedPointer(_amdReadback[bank]) * 64;
        }
        DenoisingTimestamps?.BeginPass(cmd, bank, "Denoising/ReflectionClear");
        AmdBarrier(cmd);
        var allocation = AmdReflectionGpuContract.Layout(_allocatedWidth, _allocatedHeight, _amdReduced, bank);
        uint* header = stackalloc uint[16];
        new Span<uint>(header, 16).Clear();
        header[0] = _allocatedWidth; header[1] = _allocatedHeight;
        header[2] = allocation.HitOffset; header[3] = allocation.TileOffset;
        header[5] = 1; header[6] = 1;
        header[7] = BitConverter.SingleToUInt32Bits(_settings.Reflections.SsrFullResolutionRoughness);
        header[8] = BitConverter.SingleToUInt32Bits(_settings.Reflections.SsrHalfResolutionRoughness);
        header[9] = BitConverter.SingleToUInt32Bits(_settings.Reflections.SsrQuarterResolutionRoughness);
        _context.Api.CmdUpdateBuffer(cmd, _bufferManager.GetBuffer(_amdStates[bank]), 48 * 4, 64, header);
        if (_amdReduced)
        {
            // Initialize the complete reduced domain, including inactive tile halos.
            // Previous-bank persistent history remains untouched.
            ulong pixels = (ulong)_amdWidth * _amdHeight;
            _context.Api.CmdFillBuffer(cmd, _bufferManager.GetBuffer(_amdStates[bank]), 128 * 4, pixels * 12 * 4, 0);
            _context.Api.CmdFillBuffer(cmd, _bufferManager.GetBuffer(_amdStates[0]), (128 + pixels * 12) * 4, pixels * 9 * 4, 0);
            ulong reducedAverages = (128 + pixels * 21) * 4;
            ulong tileCount = (ulong)((_amdWidth + 7) / 8) * ((_amdHeight + 7) / 8);
            _context.Api.CmdFillBuffer(cmd, _bufferManager.GetBuffer(_amdStates[0]), reducedAverages, tileCount * 8, 0);
            _context.Api.CmdFillBuffer(cmd, _bufferManager.GetBuffer(_amdStates[bank]), allocation.HitOffset * 4UL,
                (ulong)_allocatedWidth * _allocatedHeight * 4, 0);
            AmdBarrier(cmd);
            DenoisingTimestamps?.EndPass(cmd, bank);
            return;
        }
        // Persistent guide planes 0..6 are banked; planes 7..15 are shared scratch.
        ulong planeBytes = (ulong)_allocatedWidth * _allocatedHeight * 4UL;
        var current = _bufferManager.GetBuffer(_amdStates[bank]);
        _context.Api.CmdFillBuffer(cmd, current, 128UL * 4, planeBytes, 0); // producer hit distance
        _context.Api.CmdFillBuffer(cmd, current, 128UL * 4 + 5UL * planeBytes, 2UL * planeBytes, 0); // validity/sample count
        _context.Api.CmdFillBuffer(cmd, _bufferManager.GetBuffer(_amdStates[0]),
            128UL * 4 + 7UL * planeBytes, 3UL * planeBytes, 0); // non-glossy reprojection lanes
        ulong averages = 128UL * 4 + 16UL * planeBytes;
        _context.Api.CmdFillBuffer(cmd, _bufferManager.GetBuffer(_amdStates[0]), averages,
            _bufferManager.GetBufferSize(_amdStates[0]) - averages, 0);
        AmdBarrier(cmd);
        DenoisingTimestamps?.EndPass(cmd, bank);
    }
    private bool RecordAmdTemporal(CommandBuffer cmd, int bank, SceneRenderingData scene)
    {
        bool off = _effectiveDenoiser == ReflectionDenoiser.Off;
        if (!_amdActive && !off) return false;
        bool reduced = !off && _amdReduced;
        bool useTiles = !off && (reduced || scene.EffectiveReflectionImplementation == ReflectionImplementationMode.Adaptive);
        uint* push = stackalloc uint[6];
        push[0] = (uint)(BindlessIndex.AmdReflectionBufferBase + bank);
        push[1] = (uint)(BindlessIndex.AmdReflectionBufferBase + 1 - bank);
        push[2] = reduced ? _amdWidth : _allocatedWidth; push[3] = reduced ? _amdHeight : _allocatedHeight;
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
            DenoisingTimestamps?.BeginPass(cmd, bank, "Denoising/ReflectionPrepare");
            BindPipelineAndDescriptors(cmd, _amdPipelines[4], bank, bindRayScene: false);
            push[5] = reduced ? AmdReflectionGpuContract.ReducedFlag : 0;
            _context.Api.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.ComputeBit, 0, 24, push);
            scene.RecordDenoisingDispatch("AmdReflectionPrepare");
            _context.Api.CmdDispatch(cmd, (push[2] + 7) / 8, (push[3] + 7) / 8, 1);
            AmdBarrier(cmd);
            DenoisingTimestamps?.EndPass(cmd, bank);
        }
        for (int i = off ? 3 : 0; i < (off ? 4 : 3); i++)
        {
            DenoisingTimestamps?.BeginPass(cmd, bank, i switch { 0 => "Denoising/ReflectionReproject", 1 => "Denoising/ReflectionPrefilter", 2 => "Denoising/ReflectionResolve", _ => "Denoising/ReflectionCopy" });
            BindPipelineAndDescriptors(cmd, _amdPipelines[i], bank, bindRayScene: false);
            push[5] = ((uint)i + 1) | (useTiles ? 256u : 0u) | (reduced ? AmdReflectionGpuContract.ReducedFlag : 0);
            _context.Api.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.ComputeBit, 0, 24, push);
            scene.RecordDenoisingDispatch(off ? "ReflectionCopy" : "AmdReflection");
            if (reduced)
                _context.Api.CmdDispatchIndirect(cmd, _bufferManager.GetBuffer(_amdStates[bank]), AmdReflectionGpuContract.IndirectOffset);
            else DispatchActiveTilesOrScreen(cmd, bank, useTiles);
            AmdBarrier(cmd);
            DenoisingTimestamps?.EndPass(cmd, bank);
        }
        if (reduced)
        {
            DenoisingTimestamps?.BeginPass(cmd, bank, "Denoising/ReflectionReconstruct");
            BindPipelineAndDescriptors(cmd, _amdPipelines[5], bank, bindRayScene: false);
            push[5] = AmdReflectionGpuContract.ReducedFlag;
            _context.Api.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.ComputeBit, 0, 24, push);
            scene.RecordDenoisingDispatch("AmdReflectionReconstruct");
            DispatchScreen(cmd);
            AmdBarrier(cmd);
            DenoisingTimestamps?.EndPass(cmd, bank);
            var copy = new BufferCopy { SrcOffset = AmdReflectionGpuContract.IndirectOffset, Size = 4 };
            _context.Api.CmdCopyBuffer(cmd, _bufferManager.GetBuffer(_amdStates[bank]), _bufferManager.GetBuffer(_amdReadback[bank]), 1, &copy);
            _amdSubmitted[bank] = true;
        }
        scene.AmdReflectionDenoisingHalfResolution = reduced;
        scene.AmdReflectionFilterWidth = off ? 0 : push[2];
        scene.AmdReflectionFilterHeight = off ? 0 : push[3];
        scene.AmdReflectionDispatchedPixels = reduced ? _amdLastDispatchedPixels : 0;
        _amdPreviousVp = scene.ViewProjectionMatrix; _amdHistory = !off;
        return true;
    }
}
