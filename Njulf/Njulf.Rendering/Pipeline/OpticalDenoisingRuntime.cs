using System;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Resources;
using Njulf.Rendering.Utilities;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Vma;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline;

/// <summary>Owns bounded per-frame optical layers and their unfiltered temporal history.
/// All commands use the graphics queue; frame-slot reuse follows the renderer fence.</summary>
internal sealed unsafe class OpticalDenoisingRuntime : IDisposable
{
    private readonly VulkanContext _context;
    private readonly BindlessHeap _heap;
    private readonly BufferManager _buffers;
    private readonly RenderTargetManager _targets;
    private readonly RenderSettings _settings;
    private readonly GiPipelineCacheService? _cache;
    private readonly BufferHandle[] _layers = [BufferHandle.Invalid, BufferHandle.Invalid];
    private readonly BufferHandle[] _compact = [BufferHandle.Invalid, BufferHandle.Invalid];
    private ulong _compactBytes;
    private bool _compactEnabled;
    private VkPipeline _compactPrepare, _compactTemporal, _compactSpatial, _compactCorrect;
    private readonly BufferHandle[] _readback = [BufferHandle.Invalid, BufferHandle.Invalid];
    private readonly bool[] _submitted = new bool[2];
    private DescriptorSetLayout _outputLayout;
    private DescriptorPool _pool;
    private DescriptorSet _outputs;
    private PipelineLayout _layout;
    private VkPipeline _temporal, _spatial, _correct;
    private uint _width, _height, _capacity;
    private ulong _bytes;
    private int _budget, _previousBank = -1;
    private bool _historyValid, _active, _disposed;
    private Matrix4x4 _previousViewProjection = Matrix4x4.Identity;
    private ulong _sceneRevision, _cutSerial;
    private uint _materialRevision;
    private int _historySettings;
    private uint _reset = 1;
    private uint _samplingFrame;
    private string _failure = string.Empty;

    public OpticalDenoisingRuntime(VulkanContext context, BindlessHeap heap,
        BufferManager buffers, RenderTargetManager targets, RenderSettings settings,
        GiPipelineCacheService? cache)
    {
        _context = context; _heap = heap; _buffers = buffers; _targets = targets;
        _settings = settings; _cache = cache;
    }

    public BufferHandle GetLayerBuffer(int frameIndex) => _layers[frameIndex % 2];
    public ulong AllocatedBytes => (_bytes + _compactBytes) * 2 + (_readback[0].IsValid ? 64UL : 0UL);
    public string FailureDetail => _failure;

    public bool Prepare(SceneRenderingData scene)
    {
        _active = false;
        scene.OpticalDenoisingActive = false;
        scene.OpticalDenoisingAllocatedBytes = AllocatedBytes;
        if (!_settings.OpticalDenoising.Enabled ||
            !RenderFeatureIsolationPolicy.AllowsReflections(scene.ActiveFeatureIsolation) ||
            !scene.TransparentPassEnabled || scene.TransparentMeshletCount == 0)
        {
            _historyValid = false;
            return false;
        }
        EnsureResources();
        _active = _layers[0].IsValid && _correct.Handle != 0 && string.IsNullOrEmpty(_failure);
        scene.OpticalDenoisingAllocatedBytes = AllocatedBytes;
        return _active;
    }

    private void EnsureResources()
    {
        Extent2D extent = _targets.SceneColor.Extent;
        if (_width == extent.Width && _height == extent.Height && _budget == _settings.OpticalDenoising.MemoryBudgetMiB && _compactEnabled == _settings.OpticalDenoising.CompactLayers) return;
        // Only resize/settings transitions enter this cold path. It also protects
        // bindless descriptor replacement from both in-flight frame slots.
        Check(_context.Api.DeviceWaitIdle(_context.Device), "wait for optical resource replacement");
        DestroyBuffers();
        _width = extent.Width; _height = extent.Height; _budget = _settings.OpticalDenoising.MemoryBudgetMiB;
        _compactEnabled = _settings.OpticalDenoising.CompactLayers;
        _failure = string.Empty;
        try
        {
            // Reserve the tiny readback allocations inside the same budget.
            _compactBytes = _compactEnabled ? checked((64UL + (ulong)((_width + 1) / 2) * ((_height + 1) / 2) * 64UL) * 4UL) : 0UL;
            int sourceBudget = _budget - checked((int)((_compactBytes * 2 + 1048575UL) / 1048576UL));
            if (sourceBudget < 16 || _compactBytes > _context.MaximumStorageBufferRange)
                throw new InvalidOperationException("Compact optical layers exceed the configured memory/storage budget.");
            var allocation = OpticalDenoisingGpuContract.Allocation(_width, _height, sourceBudget, _context.MaximumStorageBufferRange, compactExport: _compactEnabled);
            _bytes = allocation.Bytes > 32 ? allocation.Bytes - OpticalDenoisingGpuContract.RecordWords * 4 : 0;
            _capacity = allocation.Capacity > 0 ? allocation.Capacity - 1 : 0;
            if (_capacity == 0) throw new InvalidOperationException("Optical layer headers exceed the memory/storage range budget.");
            for (int i = 0; i < 2; i++)
            {
                _layers[i] = _buffers.CreateBuffer(_bytes,
                    BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit,
                    MemoryUsage.AutoPreferDevice, debugName: $"Optical layers/history {i}", category: MemoryBudgetCategory.RenderTargets);
                _readback[i] = _buffers.CreateBuffer(32, BufferUsageFlags.TransferDstBit,
                    MemoryUsage.AutoPreferHost, AllocationCreateFlags.MappedBit | AllocationCreateFlags.HostAccessRandomBit,
                    debugName: $"Optical counters {i}", category: MemoryBudgetCategory.DiagnosticsAndDebug);
                _heap.RegisterStorageBuffer(BindlessIndex.OpticalLayerBufferBase + i, _buffers.GetBuffer(_layers[i]), 0, _bytes);
                if (_compactEnabled)
                {
                    _compact[i] = _buffers.CreateBuffer(_compactBytes, BufferUsageFlags.StorageBufferBit,
                        MemoryUsage.AutoPreferDevice, debugName: $"Compact optical layers {i}", category: MemoryBudgetCategory.RenderTargets);
                    _heap.RegisterStorageBuffer(BindlessIndex.OpticalCompactBufferBase + i, _buffers.GetBuffer(_compact[i]), 0, _compactBytes);
                }
            }
            if (_layout.Handle == 0) CreatePipelines();
            if (_compactEnabled && _compactPrepare.Handle == 0)
            {
                _compactPrepare = CreatePipeline("optical_compact_prepare.comp.spv");
                _compactTemporal = CreatePipeline("optical_compact_temporal.comp.spv");
                _compactSpatial = CreatePipeline("optical_compact_spatial.comp.spv");
                _compactCorrect = CreatePipeline("optical_compact_correct.comp.spv");
            }
            RecreateOutputDescriptors();
        }
        catch (Exception exception)
        {
            _failure = exception.Message;
            Console.Error.WriteLine($"Optical denoising disabled: {_failure}");
            DestroyBuffers();
        }
    }

    public void Begin(CommandBuffer cmd, int frameIndex, SceneRenderingData scene)
    {
        if (!Prepare(scene)) return;
        // Export is legal only after this frame's clear has actually been recorded.
        scene.OpticalDenoisingActive = true;
        int bank = frameIndex % 2;
        if (_submitted[bank])
        {
            _buffers.InvalidateBuffer(_readback[bank], 0, 32);
            uint* counters = (uint*)_buffers.GetMappedPointer(_readback[bank]);
            scene.OpticalDenoisingCapturedFragments = Math.Min(counters[5], _capacity);
            scene.OpticalDenoisingOverflowPixels = counters[6];
            scene.OpticalDenoisingHistoryReuses = counters[7];
        }
        int signature = HashCode.Combine(scene.TransparencyMode, _settings.OpticalDenoising.LayerLimit,
            _settings.OpticalDenoising.BypassFilter, _settings.Reflections.Mode,
            _settings.Transparency.ThickTransmissionMode, scene.GiTransportMaterialRevision);
        _reset = !_historyValid || _previousBank == bank || _sceneRevision != scene.SceneContentRevision ||
            _materialRevision != scene.GiTransportMaterialRevision || _cutSerial != scene.CaptureCameraCutSerial ||
            scene.HiZPolicyCameraCut != 0 || signature != _historySettings ||
            scene.HybridReflectionSourceInvalidation != ReflectionHistorySourceInvalidation.None ? 1u : 0u;
        Barrier(cmd, PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
            PipelineStageFlags2.TransferBit, AccessFlags2.TransferWriteBit);
        ulong prefix = (OpticalDenoisingGpuContract.HeaderWords + (ulong)_width * _height * (_compactEnabled ? 9u : OpticalDenoisingGpuContract.PixelWords)) * 4;
        _context.Api.CmdFillBuffer(cmd, _buffers.GetBuffer(_layers[bank]), 0, prefix, 0);
        Barrier(cmd, PipelineStageFlags2.TransferBit, AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.TransferBit, AccessFlags2.TransferWriteBit);
        uint* header = stackalloc uint[16];
        new Span<uint>(header, 16).Clear();
        header[0] = 1; header[1] = _width; header[2] = _height; header[3] = _capacity;
        header[4] = _compactEnabled ? 8u : (uint)_settings.OpticalDenoising.LayerLimit;
        header[13] = _compactEnabled ? 8u : 0u;
        header[8] = _settings.OpticalDenoising.BypassFilter ? 0u : ++_samplingFrame;
        _context.Api.CmdUpdateBuffer(cmd, _buffers.GetBuffer(_layers[bank]), 0, 64, header);
        Matrix4x4 previous = _previousViewProjection;
        _context.Api.CmdUpdateBuffer(cmd, _buffers.GetBuffer(_layers[bank]), 64, 64, &previous);
        Barrier(cmd, PipelineStageFlags2.TransferBit, AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
        _previousViewProjection = scene.ViewProjectionMatrix;
        _sceneRevision = scene.SceneContentRevision; _materialRevision = scene.GiTransportMaterialRevision;
        _cutSerial = scene.CaptureCameraCutSerial; _historySettings = signature;
    }

    public bool Active => _active;

    public void Record(CommandBuffer cmd, int frameIndex, SceneRenderingData scene, int stage)
    {
        if (!_active) return;
        if (stage is 1 or 2 && _settings.OpticalDenoising.BypassFilter) return;
        int bank = frameIndex % 2;
        Barrier(cmd, PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryWriteBit,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
        DescriptorSet storage = _heap.StorageBufferSet;
        _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _layout, 0, 1, &storage, 0, null);
        uint* push = stackalloc uint[8];
        new Span<uint>(push, 8).Clear();
        push[0] = (uint)(BindlessIndex.OpticalLayerBufferBase + bank);
        push[1] = (uint)(BindlessIndex.OpticalLayerBufferBase + (1 - bank));
        push[3] = _reset; push[4] = scene.TransparencyMode == TransparencyMode.WeightedBlendedOit ? 1u : 0u;
        push[5] = _settings.OpticalDenoising.BypassFilter ? 1u : 0u;
        if (_compactEnabled)
        {
            push[6] = push[0];
            push[0] = (uint)(BindlessIndex.OpticalCompactBufferBase + bank);
            push[1] = (uint)(BindlessIndex.OpticalCompactBufferBase + 1 - bank);
        }
        if (stage == 1)
        {
            if (_compactEnabled)
            {
                Dispatch(cmd, _compactPrepare, push, true);
                Barrier(cmd, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit,
                    PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
            }
            Dispatch(cmd, _compactEnabled ? _compactTemporal : _temporal, push, _compactEnabled);
        }
        else if (stage == 2)
        {
            push[2] = 1; Dispatch(cmd, _compactEnabled ? _compactSpatial : _spatial, push, _compactEnabled);
            Barrier(cmd, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
            push[2] = 2; Dispatch(cmd, _compactEnabled ? _compactSpatial : _spatial, push, _compactEnabled);
        }
        else
        {
            _targets.SceneColor.TransitionToStorageReadWrite(cmd);
            _targets.WeightedOitAccumulation.TransitionToStorageReadWrite(cmd);
            DescriptorSet output = _outputs;
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _layout, 1, 1, &output, 0, null);
            Dispatch(cmd, _compactEnabled ? _compactCorrect : _correct, push);
            _targets.SceneColor.TransitionToColorAttachment(cmd);
            _targets.WeightedOitAccumulation.TransitionToShaderRead(cmd);
            Barrier(cmd, PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
                AccessFlags2.ShaderStorageWriteBit, PipelineStageFlags2.TransferBit, AccessFlags2.TransferReadBit);
            var copy = new BufferCopy { Size = 32 };
            _context.Api.CmdCopyBuffer(cmd, _buffers.GetBuffer(_layers[bank]), _buffers.GetBuffer(_readback[bank]), 1, &copy);
            _submitted[bank] = true; _historyValid = !_settings.OpticalDenoising.BypassFilter; _previousBank = bank;
        }
    }

    private void Dispatch(CommandBuffer cmd, VkPipeline pipeline, uint* push, bool halfResolution = false)
    {
        _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, pipeline);
        _context.Api.CmdPushConstants(cmd, _layout, ShaderStageFlags.ComputeBit, 0, 32, push);
        uint width = halfResolution ? (_width + 1) / 2 : _width;
        uint height = halfResolution ? (_height + 1) / 2 : _height;
        _context.Api.CmdDispatch(cmd, (width + 7) / 8, (height + 7) / 8, 1);
    }

    private void CreatePipelines()
    {
        var bindings = stackalloc DescriptorSetLayoutBinding[2];
        for (uint i = 0; i < 2; i++) bindings[i] = new DescriptorSetLayoutBinding
        { Binding = i, DescriptorType = DescriptorType.StorageImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit };
        var setInfo = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 2, PBindings = bindings };
        Check(_context.Api.CreateDescriptorSetLayout(_context.Device, &setInfo, null, out _outputLayout), "create optical output layout");
        var layouts = stackalloc DescriptorSetLayout[2] { _heap.StorageBufferSetLayout, _outputLayout };
        var range = new PushConstantRange { StageFlags = ShaderStageFlags.ComputeBit, Size = 32 };
        var layoutInfo = new PipelineLayoutCreateInfo
        { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 2, PSetLayouts = layouts, PushConstantRangeCount = 1, PPushConstantRanges = &range };
        Check(_context.Api.CreatePipelineLayout(_context.Device, &layoutInfo, null, out _layout), "create optical pipeline layout");
        _temporal = CreatePipeline("optical_temporal.comp.spv");
        _spatial = CreatePipeline("optical_spatial.comp.spv");
        _correct = CreatePipeline("optical_correct.comp.spv");
    }

    private VkPipeline CreatePipeline(string shader)
    {
        ShaderModule module = ShaderModuleLoader.Load(_context, shader);
        nint entry = SilkMarshal.StringToPtr("main");
        try
        {
            var info = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo, Layout = _layout, BasePipelineIndex = -1,
                Stage = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit, Module = module, PName = (byte*)entry }
            };
            Result result = _cache != null
                ? _cache.CreateComputePipeline(new PipelineArtifactId($"Optical:{shader}"), &info, out VkPipeline pipeline)
                : _context.Api.CreateComputePipelines(_context.Device, default, 1, &info, null, out pipeline);
            Check(result, $"create {shader}");
            _context.SetDebugName(pipeline.Handle, ObjectType.Pipeline, shader);
            return pipeline;
        }
        finally { SilkMarshal.Free(entry); _context.Api.DestroyShaderModule(_context.Device, module, null); }
    }

    private void RecreateOutputDescriptors()
    {
        if (_pool.Handle != 0) _context.Api.DestroyDescriptorPool(_context.Device, _pool, null);
        var size = new DescriptorPoolSize { Type = DescriptorType.StorageImage, DescriptorCount = 2 };
        var info = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, PoolSizeCount = 1, PPoolSizes = &size, MaxSets = 1 };
        Check(_context.Api.CreateDescriptorPool(_context.Device, &info, null, out _pool), "create optical descriptor pool");
        DescriptorSetLayout layout = _outputLayout;
        var allocation = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool, DescriptorSetCount = 1, PSetLayouts = &layout };
        Check(_context.Api.AllocateDescriptorSets(_context.Device, &allocation, out _outputs), "allocate optical descriptors");
        var images = stackalloc DescriptorImageInfo[2];
        images[0] = new DescriptorImageInfo { ImageView = _targets.SceneColor.View, ImageLayout = ImageLayout.General };
        images[1] = new DescriptorImageInfo { ImageView = _targets.WeightedOitAccumulation.View, ImageLayout = ImageLayout.General };
        var writes = stackalloc WriteDescriptorSet[2];
        for (uint i = 0; i < 2; i++) writes[i] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet,
            DstSet = _outputs, DstBinding = i, DescriptorCount = 1, DescriptorType = DescriptorType.StorageImage, PImageInfo = images + i };
        _context.Api.UpdateDescriptorSets(_context.Device, 2, writes, 0, null);
    }

    private void Barrier(CommandBuffer cmd, PipelineStageFlags2 source, AccessFlags2 sourceAccess,
        PipelineStageFlags2 target, AccessFlags2 targetAccess)
    {
        var barrier = new MemoryBarrier2 { SType = StructureType.MemoryBarrier2, SrcStageMask = source,
            SrcAccessMask = sourceAccess, DstStageMask = target, DstAccessMask = targetAccess };
        var dependency = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &barrier };
        _context.Api.CmdPipelineBarrier2(cmd, &dependency);
    }

    public void OnTargetsRecreated() { _width = 0; _historyValid = false; }
    private void DestroyBuffers()
    {
        for (int i = 0; i < 2; i++)
        {
            if (_layers[i].IsValid) _buffers.DestroyBuffer(_layers[i]);
            if (_compact[i].IsValid) _buffers.DestroyBuffer(_compact[i]);
            _compact[i] = BufferHandle.Invalid;
            if (_readback[i].IsValid) _buffers.DestroyBuffer(_readback[i]);
            _layers[i] = _readback[i] = BufferHandle.Invalid; _submitted[i] = false;
        }
        _bytes = _compactBytes = 0; _historyValid = false; _active = false; _previousBank = -1;
    }
    private static void Check(Result result, string operation)
    { if (result != Result.Success) throw new VulkanException($"Failed to {operation}", result); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DestroyBuffers();
        foreach (VkPipeline pipeline in new[] { _temporal, _spatial, _correct, _compactPrepare, _compactTemporal, _compactSpatial, _compactCorrect })
            if (pipeline.Handle != 0) _context.Api.DestroyPipeline(_context.Device, pipeline, null);
        if (_pool.Handle != 0) _context.Api.DestroyDescriptorPool(_context.Device, _pool, null);
        if (_layout.Handle != 0) _context.Api.DestroyPipelineLayout(_context.Device, _layout, null);
        if (_outputLayout.Handle != 0) _context.Api.DestroyDescriptorSetLayout(_context.Device, _outputLayout, null);
    }
}

internal sealed class OpticalDenoisingPass : RenderPassBase
{
    private readonly OpticalDenoisingRuntime _runtime;
    private readonly int _stage;
    public OpticalDenoisingPass(string name, int stage, VulkanContext context, SwapchainManager swapchain,
        BindlessHeap heap, OpticalDenoisingRuntime runtime) : base(name, context, swapchain, heap)
    { _runtime = runtime; _stage = stage; }
    public override RenderGraphQueueIntent QueueIntent => RenderGraphQueueIntent.Compute;
    public override bool SupportsAsyncCompute => false;
    // Prepare creates resources only when a scene actually draws transparency.
    public override void Initialize() { }
    public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData) => _runtime.Active;
    public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
    { if (_stage == 0) _runtime.Begin(cmd, frameIndex, sceneData); else _runtime.Record(cmd, frameIndex, sceneData, _stage); }
    public override void OnSwapchainRecreated() { if (_stage == 0) _runtime.OnTargetsRecreated(); }
    public override void Cleanup() { if (_stage == 0) _runtime.Dispose(); }
}
