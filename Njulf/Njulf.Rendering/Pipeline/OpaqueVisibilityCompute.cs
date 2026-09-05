using System;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline;

/// <summary>
/// Owns the experimental opaque work queues, descriptors, and compute programs.
/// Queue storage is replaced only for the frame slot whose fence has completed.
/// </summary>
internal sealed unsafe class OpaqueVisibilityCompute : IDisposable
{
    private readonly VulkanContext _context;
    private readonly BindlessHeap _heap;
    private readonly BufferManager _buffers;
    private readonly RenderTargetManager _targets;
    private readonly GiPipelineCacheService? _cache;
    private readonly uint _performanceMask;
    private readonly DescriptorSet[] _sets = new DescriptorSet[2];
    private readonly BufferHandle[,] _work = new BufferHandle[2, 3];
    private readonly ulong[] _capacity = new ulong[2];
    private readonly VkPipeline[,] _shade = new VkPipeline[3, 3];
    private DescriptorSetLayout _setLayout;
    private DescriptorPool _pool;
    private PipelineLayout _layout;
    private Sampler _sampler;
    private VkPipeline _classify;
    private VkPipeline _prefix;
    private VkPipeline _scatter;
    private nint _entry;

    internal uint PerformanceMask => _performanceMask;

    internal static bool SupportsComputeQuads(VulkanContext context)
    {
        var subgroup = new PhysicalDeviceSubgroupProperties { SType = StructureType.PhysicalDeviceSubgroupProperties };
        var properties = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &subgroup };
        context.Api.GetPhysicalDeviceProperties2(context.PhysicalDevice, &properties);
        const SubgroupFeatureFlags required = SubgroupFeatureFlags.BasicBit |
            SubgroupFeatureFlags.BallotBit | SubgroupFeatureFlags.QuadBit;
        return (subgroup.SupportedStages & ShaderStageFlags.ComputeBit) != 0 &&
            (subgroup.SupportedOperations & required) == required &&
            subgroup.SubgroupSize >= 4 && subgroup.SubgroupSize % 4 == 0;
    }

    internal OpaqueVisibilityCompute(VulkanContext context, BindlessHeap heap, BufferManager buffers,
        RenderTargetManager targets, GiPipelineCacheService? cache, uint performanceMask)
    {
        _context = context;
        _heap = heap;
        _buffers = buffers;
        _targets = targets;
        _cache = cache;
        _performanceMask = performanceMask;
        try
        {
            _entry = SilkMarshal.StringToPtr("main");
            var bindings = stackalloc DescriptorSetLayoutBinding[7];
            for (uint i = 0; i < 7; ++i)
                bindings[i] = new DescriptorSetLayoutBinding
                {
                    Binding = i, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit,
                    DescriptorType = i == 0 ? DescriptorType.CombinedImageSampler :
                        i < 4 ? DescriptorType.StorageImage : DescriptorType.StorageBuffer
                };
            var setInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 7, PBindings = bindings
            };
            Check(_context.Api.CreateDescriptorSetLayout(_context.Device, &setInfo, null, out _setLayout), "descriptor layout");
            var sizes = stackalloc DescriptorPoolSize[3]
            {
                new(DescriptorType.CombinedImageSampler, 2), new(DescriptorType.StorageImage, 6), new(DescriptorType.StorageBuffer, 6)
            };
            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 2, PoolSizeCount = 3, PPoolSizes = sizes
            };
            Check(_context.Api.CreateDescriptorPool(_context.Device, &poolInfo, null, out _pool), "descriptor pool");
            var layouts = stackalloc DescriptorSetLayout[3] { _heap.StorageBufferSetLayout, _heap.TextureSamplerSetLayout, _setLayout };
            var range = new PushConstantRange { StageFlags = ShaderStageFlags.ComputeBit, Size = (uint)sizeof(GPUForwardPushConstants) };
            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 3, PSetLayouts = layouts,
                PushConstantRangeCount = 1, PPushConstantRanges = &range
            };
            Check(_context.Api.CreatePipelineLayout(_context.Device, &layoutInfo, null, out _layout), "pipeline layout");
            var outputLayouts = stackalloc DescriptorSetLayout[2] { _setLayout, _setLayout };
            var allocation = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _pool,
                DescriptorSetCount = 2, PSetLayouts = outputLayouts
            };
            fixed (DescriptorSet* sets = _sets)
                Check(_context.Api.AllocateDescriptorSets(_context.Device, &allocation, sets), "descriptor sets");
            var samplerInfo = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo, MagFilter = Filter.Nearest, MinFilter = Filter.Nearest,
                MipmapMode = SamplerMipmapMode.Nearest, AddressModeU = SamplerAddressMode.ClampToEdge,
                AddressModeV = SamplerAddressMode.ClampToEdge, AddressModeW = SamplerAddressMode.ClampToEdge, MaxLod = 0
            };
            Check(_context.Api.CreateSampler(_context.Device, &samplerInfo, null, out _sampler), "visibility sampler");
            _classify = CreatePipeline("opaque_visibility_classify.comp.spv");
            _prefix = CreatePipeline("opaque_visibility_prefix.comp.spv");
            _scatter = CreatePipeline("opaque_visibility_scatter.comp.spv");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void Record(CommandBuffer cmd, int frame, GPUForwardPushConstants push, bool hybrid, bool sparse)
    {
        int bank = hybrid ? sparse ? 2 : 1 : 0;
        for (int family = 0; family < 3; ++family)
        {
            if (_shade[bank, family].Handle != 0) continue;
            string suffix = bank == 0 ? "" : bank == 1 ? "_hybrid" : "_hybrid_sparse";
            _shade[bank, family] = CreatePipeline($"opaque_shade_{family}{suffix}.comp.spv", forwardProgram: true);
        }
        uint width = (uint)push.ScreenDimensions.X;
        uint height = (uint)push.ScreenDimensions.Y;
        EnsureFrameStorage(frame, OpaqueVisibilityComputePolicy.PixelCapacity(width, height));
        RewriteDescriptors(frame, hybrid);
        _targets.OpaqueVisibility!.TransitionToShaderRead(cmd);
        _targets.SceneColor.TransitionToStorageWrite(cmd);
        if (hybrid)
        {
            _targets.HybridReflectionReceiverPayload!.TransitionToStorageWrite(cmd);
            if (!sparse) _targets.HybridReflectionRawMetadata!.TransitionToStorageWrite(cmd);
        }
        var control = _buffers.GetBuffer(_work[frame, 2]);
        _context.Api.CmdFillBuffer(cmd, control, 0, OpaqueVisibilityComputePolicy.ControlBytes, 0);
        Barrier(cmd, PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryWriteBit,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderReadBit | AccessFlags2.ShaderWriteBit);
        var sets = stackalloc DescriptorSet[3] { _heap.StorageBufferSet, _heap.TextureSamplerSet, _sets[frame] };
        _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _layout, 0, 3, sets, 0, null);
        _context.Api.CmdPushConstants(cmd, _layout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(GPUForwardPushConstants), &push);
        _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _classify);
        _context.Api.CmdDispatch(cmd, (width + 15) / 16, (height + 15) / 16, 1);
        ComputeBarrier(cmd);
        _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _prefix);
        _context.Api.CmdDispatch(cmd, 1, 1, 1);
        ComputeBarrier(cmd);
        _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _scatter);
        uint scatterGroups = checked((uint)((_capacity[frame] + 63) / 64));
        _context.Api.CmdDispatch(cmd, Math.Min(scatterGroups, 65535u), (scatterGroups + 65534u) / 65535u, 1);
        Barrier(cmd, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderWriteBit,
            PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.DrawIndirectBit,
            AccessFlags2.ShaderReadBit | AccessFlags2.ShaderWriteBit | AccessFlags2.IndirectCommandReadBit);
        for (uint family = 0; family < 3; ++family)
        {
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _shade[bank, family]);
            _context.Api.CmdDispatchIndirect(cmd, control, (OpaqueVisibilityComputePolicy.IndirectWord + family * 3u) * sizeof(uint));
        }
        Barrier(cmd, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderWriteBit,
            PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit);
        _targets.SceneColor.TransitionToColorAttachment(cmd);
    }

    private void EnsureFrameStorage(int frame, ulong pixels)
    {
        if (_capacity[frame] == pixels) return;
        // The renderer has waited this frame slot's fence before recording it.
        for (int i = 0; i < 3; ++i)
        {
            if (_work[frame, i].IsValid) _buffers.DestroyBuffer(_work[frame, i]);
            _work[frame, i] = BufferHandle.Invalid;
        }
        _capacity[frame] = 0;
        _work[frame, 0] = DeviceBuffer(checked(pixels * OpaqueVisibilityComputePolicy.JobBytes), BufferUsageFlags.StorageBufferBit, $"Opaque visibility jobs {frame}");
        _work[frame, 1] = DeviceBuffer(checked(pixels * OpaqueVisibilityComputePolicy.IndexBytes), BufferUsageFlags.StorageBufferBit, $"Opaque visibility indices {frame}");
        _work[frame, 2] = DeviceBuffer(OpaqueVisibilityComputePolicy.ControlBytes,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.IndirectBufferBit, $"Opaque visibility control {frame}");
        _capacity[frame] = pixels;
    }

    private BufferHandle DeviceBuffer(ulong size, BufferUsageFlags usage, string name) =>
        _buffers.CreateDeviceBuffer(size, usage, requireDeviceAddress: false, MemoryBudgetCategory.GlobalIllumination, name);

    private void RewriteDescriptors(int frame, bool hybrid)
    {
        var images = stackalloc DescriptorImageInfo[4];
        images[0] = new DescriptorImageInfo(_sampler, _targets.OpaqueVisibility!.View, ImageLayout.ShaderReadOnlyOptimal);
        images[1] = new DescriptorImageInfo(default, _targets.SceneColor.View, ImageLayout.General);
        if (hybrid)
        {
            images[2] = new DescriptorImageInfo(default, _targets.HybridReflectionReceiverPayload!.View, ImageLayout.General);
            images[3] = new DescriptorImageInfo(default, _targets.HybridReflectionRawMetadata!.View, ImageLayout.General);
        }
        var buffers = stackalloc DescriptorBufferInfo[3];
        buffers[0] = new DescriptorBufferInfo(_buffers.GetBuffer(_work[frame, 0]), 0, _capacity[frame] * OpaqueVisibilityComputePolicy.JobBytes);
        buffers[1] = new DescriptorBufferInfo(_buffers.GetBuffer(_work[frame, 1]), 0, _capacity[frame] * OpaqueVisibilityComputePolicy.IndexBytes);
        buffers[2] = new DescriptorBufferInfo(_buffers.GetBuffer(_work[frame, 2]), 0, OpaqueVisibilityComputePolicy.ControlBytes);
        var writes = stackalloc WriteDescriptorSet[7];
        uint count = 0;
        for (uint binding = 0; binding < 7; ++binding)
        {
            if (!hybrid && (binding == 2 || binding == 3)) continue;
            writes[count++] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstSet = _sets[frame], DstBinding = binding,
                DescriptorCount = 1, DescriptorType = binding == 0 ? DescriptorType.CombinedImageSampler :
                    binding < 4 ? DescriptorType.StorageImage : DescriptorType.StorageBuffer,
                PImageInfo = binding < 4 ? &images[binding] : null,
                PBufferInfo = binding >= 4 ? &buffers[binding - 4] : null
            };
        }
        _context.Api.UpdateDescriptorSets(_context.Device, count, writes, 0, null);
    }

    private VkPipeline CreatePipeline(string name, bool forwardProgram = false)
    {
        ShaderModule module = ShaderModuleLoader.Load(_context, name);
        try
        {
            uint performanceMask = _performanceMask;
            var entry = new SpecializationMapEntry
            {
                ConstantID = MeshPipeline.ForwardPerformanceSpecializationConstantId,
                Offset = 0, Size = sizeof(uint)
            };
            var specialization = new SpecializationInfo
            {
                MapEntryCount = 1, PMapEntries = &entry,
                DataSize = sizeof(uint), PData = &performanceMask
            };
            var stage = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ComputeBit,
                Module = module, PName = (byte*)_entry,
                PSpecializationInfo = forwardProgram ? &specialization : null
            };
            var info = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo, Stage = stage, Layout = _layout, BasePipelineIndex = -1
            };
            Result result = _cache != null
                ? _cache.CreateComputePipeline(new PipelineArtifactId($"OpaqueVisibility:{name}.performance-{_performanceMask:x8}"), &info, out VkPipeline pipeline)
                : _context.Api.CreateComputePipelines(_context.Device, default, 1, &info, null, out pipeline);
            Check(result, name);
            return pipeline;
        }
        finally { _context.Api.DestroyShaderModule(_context.Device, module, null); }
    }

    private void Check(Result result, string operation)
    {
        if (result != Result.Success) throw new VulkanException($"Opaque visibility {operation} failed", result);
    }

    private void ComputeBarrier(CommandBuffer cmd) => Barrier(cmd, PipelineStageFlags2.ComputeShaderBit,
        AccessFlags2.ShaderWriteBit, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderReadBit | AccessFlags2.ShaderWriteBit);

    private void Barrier(CommandBuffer cmd, PipelineStageFlags2 source, AccessFlags2 sourceAccess,
        PipelineStageFlags2 destination, AccessFlags2 destinationAccess)
    {
        var memory = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2, SrcStageMask = source, SrcAccessMask = sourceAccess,
            DstStageMask = destination, DstAccessMask = destinationAccess
        };
        var dependency = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &memory };
        _context.Api.CmdPipelineBarrier2(cmd, &dependency);
    }

    public void Dispose()
    {
        foreach (VkPipeline pipeline in _shade)
            if (pipeline.Handle != 0) _context.Api.DestroyPipeline(_context.Device, pipeline, null);
        Array.Clear(_shade);
        if (_classify.Handle != 0) _context.Api.DestroyPipeline(_context.Device, _classify, null);
        if (_prefix.Handle != 0) _context.Api.DestroyPipeline(_context.Device, _prefix, null);
        if (_scatter.Handle != 0) _context.Api.DestroyPipeline(_context.Device, _scatter, null);
        _classify = _prefix = _scatter = default;
        if (_pool.Handle != 0) _context.Api.DestroyDescriptorPool(_context.Device, _pool, null);
        if (_layout.Handle != 0) _context.Api.DestroyPipelineLayout(_context.Device, _layout, null);
        if (_setLayout.Handle != 0) _context.Api.DestroyDescriptorSetLayout(_context.Device, _setLayout, null);
        if (_sampler.Handle != 0) _context.Api.DestroySampler(_context.Device, _sampler, null);
        _pool = default; _layout = default; _setLayout = default; _sampler = default;
        if (_entry != 0) SilkMarshal.Free(_entry);
        _entry = 0;
        for (int frame = 0; frame < 2; ++frame)
        for (int i = 0; i < 3; ++i)
        {
            if (_work[frame, i].IsValid) _buffers.DestroyBuffer(_work[frame, i]);
            _work[frame, i] = BufferHandle.Invalid;
        }
        Array.Clear(_capacity);
    }
}
