using System;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;
using Vma;

namespace Njulf.Rendering.Pipeline;

/// <summary>Private, nonaliased state for AMD's per-light shadow denoiser.
/// Both history banks and all dispatches are serialized on the graphics queue.</summary>
internal sealed unsafe class AmdShadowDenoiser : IDisposable
{
    private readonly VulkanContext _context;
    private readonly BindlessHeap _heap;
    private readonly BufferManager _buffers;
    private readonly RenderTargetManager _targets;
    private readonly GiPipelineCacheService? _cache;
    private readonly BufferHandle[] _states = new BufferHandle[8];
    private readonly uint[] _identities = new uint[4];
    private readonly int[] _signatures = new int[4];
    private readonly DirectionalShadowComputePipeline?[] _pipelines = new DirectionalShadowComputePipeline?[4];
    private uint _width, _height;
    private uint _nativeWidth, _nativeHeight;
    private ulong _bytes, _serial = ulong.MaxValue, _cut;
    private Matrix4x4 _previousVp = Matrix4x4.Identity;
    private int _previousBank = -1;
    private bool _failed;
    private readonly bool _subgroupsSupported;
    public bool Active { get; private set; }
    public string FailureDetail { get; private set; } = string.Empty;
    public ulong AllocatedBytes { get; private set; }

    public AmdShadowDenoiser(VulkanContext context, BindlessHeap heap, BufferManager buffers,
        RenderTargetManager targets, GiPipelineCacheService? cache)
    {
        _context = context; _heap = heap; _buffers = buffers; _targets = targets; _cache = cache;
        Array.Fill(_states, BufferHandle.Invalid);
        var subgroup = new PhysicalDeviceSubgroupProperties { SType = StructureType.PhysicalDeviceSubgroupProperties };
        var properties = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &subgroup };
        context.Api.GetPhysicalDeviceProperties2(context.PhysicalDevice, &properties);
        const SubgroupFeatureFlags required = SubgroupFeatureFlags.BasicBit | SubgroupFeatureFlags.VoteBit | SubgroupFeatureFlags.QuadBit;
        _subgroupsSupported = (subgroup.SupportedOperations & required) == required &&
            (subgroup.SupportedStages & ShaderStageFlags.ComputeBit) != 0;
    }

    public bool Prepare(SceneRenderingData scene, bool enabled)
    {
        Active = false;
        scene.AreaDenoisingActive = false;
        scene.AreaDenoisingAllocatedBytes = AllocatedBytes;
        if (!enabled || !_context.ShaderFloat16Enabled || !_subgroupsSupported || _failed) return false;
        try
        {
            if (_nativeWidth != scene.ScreenWidth || _nativeHeight != scene.ScreenHeight)
            {
                Check(_context.Api.DeviceWaitIdle(_context.Device), "replace AMD shadow buffers");
                DestroyBuffers();
                _nativeWidth = scene.ScreenWidth; _nativeHeight = scene.ScreenHeight;
                _width = (_nativeWidth + 1) / 2; _height = (_nativeHeight + 1) / 2;
                ulong words = 128UL + (ulong)_width * _height * 8 +
                    (ulong)((_width + 7) / 8) * ((_height + 7) / 8 + (_height + 3) / 4);
                _bytes = checked(words * 4);
                if (_bytes > _context.MaximumStorageBufferRange)
                    throw new InvalidOperationException("AMD shadow state exceeds the storage-buffer range.");
            }
            for (int slot = 0; slot < scene.AreaShadowSelectedCount; slot++)
                for (int bank = 0; bank < 2; bank++)
                {
                    int index = bank * 4 + slot;
                    if (_states[index].IsValid) continue;
                    _states[index] = _buffers.CreateBuffer(_bytes,
                        BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                        MemoryUsage.AutoPreferDevice, debugName: $"AMD shadow {bank}:{slot}",
                        category: MemoryBudgetCategory.RenderTargets);
                    _heap.RegisterStorageBuffer(BindlessIndex.AmdShadowBufferBase + index,
                        _buffers.GetBuffer(_states[index]), 0, _bytes);
                    AllocatedBytes += _bytes;
                    _serial = ulong.MaxValue;
                }
            if (_pipelines[0] == null)
            {
                string[] names = ["prepare", "classify", "filter", "publish"];
                for (int i = 0; i < names.Length; i++)
                    _pipelines[i] = new DirectionalShadowComputePipeline(_context, _heap,
                        $"amd_shadow_{names[i]}.comp.spv", 32, _cache);
            }
            Active = scene.AreaShadowSelectedCount > 0;
        }
        catch (Exception exception)
        {
            _failed = true; FailureDetail = exception.Message;
            Console.Error.WriteLine($"AMD shadow denoising unavailable: {FailureDetail}");
        }
        scene.AreaDenoisingActive = Active;
        scene.AreaDenoisingAllocatedBytes = AllocatedBytes;
        return Active;
    }

    internal Njulf.Rendering.Debug.GpuTimestampRecorder? DenoisingTimestamps { get; set; }
    private int _timingFrame;
    private SceneRenderingData? _dispatchScene;
    public void Record(CommandBuffer cmd, int frameIndex, SceneRenderingData scene)
    {
        if (!Active) return;
        _timingFrame = frameIndex; _dispatchScene = scene;
        _targets.SceneDepth.TransitionToDepthReadOnly(cmd);
        _targets.MotionVectors.TransitionToShaderRead(cmd);
        int bank = frameIndex % 2;
        bool reset = _serial == ulong.MaxValue || scene.DdgiFrameSerial != _serial + 1 ||
            _previousBank == bank || _cut != scene.CaptureCameraCutSerial ||
            scene.HiZPolicyCameraCut != 0 || scene.MotionVectorsEnabled == 0;
        uint* header = stackalloc uint[64];
        new Span<uint>(header, 64).Clear();
        *(Matrix4x4*)header = scene.InverseProjectionMatrix;
        *(Matrix4x4*)(header + 16) = scene.InverseViewProjectionMatrix;
        *(Matrix4x4*)(header + 32) = scene.InverseViewProjectionMatrix * _previousVp;
        *(Vector4*)(header + 48) = new Vector4(scene.CameraPosition, 1);
        header[52] = _nativeWidth; header[53] = _nativeHeight;
        uint* pushes = stackalloc uint[4 * 8];
        Barrier(cmd);
        Span<uint> nextIds = stackalloc uint[4];
        Span<int> nextSignatures = stackalloc int[4];
        nextIds.Clear(); nextSignatures.Clear();
        for (int slot = 0; slot < scene.AreaShadowSelectedCount; slot++)
        {
            uint* push = pushes + slot * 8;
            SelectedLocalShadow selected = scene.AreaShadowLights[slot];
            int signature = HashCode.Combine(selected.Light.Position, selected.Light.Direction,
                selected.Light.Up, selected.Light.Size, selected.Light.Type, selected.Light.Range);
            int previousSlot = Array.IndexOf(_identities, selected.StableIdentity);
            bool lightReset = reset || previousSlot < 0 || _signatures[previousSlot] != signature;
            if (previousSlot < 0) previousSlot = slot;
            int index = bank * 4 + slot;
            push[0] = (uint)(BindlessIndex.AmdShadowBufferBase + index);
            push[1] = (uint)(BindlessIndex.AmdShadowBufferBase + (1 - bank) * 4 + previousSlot);
            push[2] = (uint)(BindlessIndex.AreaRayShadowMaskBufferBase + frameIndex);
            push[3] = _width; push[4] = _height; push[5] = (uint)slot; push[6] = lightReset ? 1u : 0u;
            _context.Api.CmdUpdateBuffer(cmd, _buffers.GetBuffer(_states[index]), 0, 256, header);
            nextIds[slot] = selected.StableIdentity; nextSignatures[slot] = signature;
        }
        // Independent light buffers can run together. Only stage boundaries need visibility.
        ReadOnlySpan<int> stages = [0, 1, 3, 4, 5];
        foreach (int stage in stages)
        {
            Barrier(cmd);
            DenoisingTimestamps?.BeginPass(cmd, _timingFrame, $"Denoising/ShadowStage{stage}");
            for (int slot = 0; slot < scene.AreaShadowSelectedCount; slot++)
                Dispatch(cmd, stage < 2 ? stage : 2, pushes + slot * 8, (uint)stage);
            DenoisingTimestamps?.EndPass(cmd, _timingFrame);
        }
        Barrier(cmd);
        // Publish all visibility bytes in one owner invocation per native pixel.
        pushes[5] = (uint)scene.AreaShadowSelectedCount;
        DenoisingTimestamps?.BeginPass(cmd, _timingFrame, "Denoising/ShadowPublish");
        Dispatch(cmd, 3, pushes, 6);
        DenoisingTimestamps?.EndPass(cmd, _timingFrame);
        nextIds.CopyTo(_identities); nextSignatures.CopyTo(_signatures);
        _previousVp = scene.ViewProjectionMatrix; _previousBank = bank;
        _serial = scene.DdgiFrameSerial; _cut = scene.CaptureCameraCutSerial;
        Barrier(cmd);
    }

    private void Dispatch(CommandBuffer cmd, int pipelineIndex, uint* push, uint stage)
    {
        _dispatchScene?.RecordDenoisingDispatch("AmdShadow");
        var pipeline = _pipelines[pipelineIndex]!;
        DescriptorSet* sets = stackalloc DescriptorSet[2] { _heap.StorageBufferSet, _heap.TextureSamplerSet };
        _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, pipeline.Pipeline);
        _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, pipeline.Layout, 0, 2, sets, 0, null);
        push[7] = stage;
        _context.Api.CmdPushConstants(cmd, pipeline.Layout, ShaderStageFlags.ComputeBit, 0, 32, push);
        uint width = stage == 6 ? _nativeWidth : _width;
        uint height = stage == 6 ? _nativeHeight : _height;
        _context.Api.CmdDispatch(cmd, (width + 7) / 8, (height + 7) / 8, 1);
    }

    private void Barrier(CommandBuffer cmd)
    {
        var barrier = new MemoryBarrier2 { SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.TransferBit, SrcAccessMask = AccessFlags2.ShaderReadBit | AccessFlags2.ShaderWriteBit | AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.TransferBit | PipelineStageFlags2.FragmentShaderBit, DstAccessMask = AccessFlags2.ShaderReadBit | AccessFlags2.ShaderWriteBit | AccessFlags2.TransferWriteBit };
        var info = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &barrier };
        _context.Api.CmdPipelineBarrier2(cmd, &info);
    }
    private static void Check(Result result, string operation)
    { if (result != Result.Success) throw new VulkanException(operation, result); }
    private void DestroyBuffers()
    {
        foreach (var state in _states) if (state.IsValid) _buffers.DestroyBuffer(state);
        Array.Fill(_states, BufferHandle.Invalid); AllocatedBytes = 0;
        _serial = ulong.MaxValue; Array.Clear(_identities); Array.Clear(_signatures);
    }
    public void Dispose()
    { DestroyBuffers(); foreach (var pipeline in _pipelines) pipeline?.Dispose(); }
}

internal sealed class AreaShadowDenoisePass : RenderPassBase
{
    private readonly AreaRayShadowPass _source;
    public AreaShadowDenoisePass(VulkanContext context, SwapchainManager swapchain, BindlessHeap heap,
        AreaRayShadowPass source) : base("AreaShadowDenoisePass", context, swapchain, heap) => _source = source;
    public override bool SupportsSecondaryCommandBuffer => false;
    public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData) =>
        sceneData.AreaRayShadowPassEnabled && sceneData.AreaDenoisingActive;
    public override void Initialize() { }
    public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData) =>
        _source.RecordDenoising(cmd, frameIndex, sceneData);
    public override void Execute(CommandBuffer cmd, int frame, SceneRenderingData scene, Njulf.Rendering.Debug.GpuTimestampRecorder? timestamps) => _source.RecordDenoising(cmd, frame, scene, timestamps);
    public override void Cleanup() { }
}
