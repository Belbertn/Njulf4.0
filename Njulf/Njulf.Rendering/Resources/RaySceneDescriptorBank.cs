using System;
using Njulf.Rendering.Core;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Renderer-owned descriptor bank for the shared ray scene.  Compute and
/// fragment consumers use the same set layout and frame-slot TLAS publication,
/// which prevents independently managed descriptors from observing different
/// acceleration-structure generations.
/// </summary>
public sealed unsafe class RaySceneDescriptorBank : IDisposable
{
    private readonly VulkanContext _context;
    private readonly AccelerationStructureManager _accelerationStructures;
    private readonly DescriptorSet[] _sets =
        new DescriptorSet[RenderingConstants.FramesInFlight];
    private readonly AccelerationStructureKHR[] _boundTlases =
        new AccelerationStructureKHR[RenderingConstants.FramesInFlight];

    private DescriptorSetLayout _layout;
    private DescriptorPool _pool;
    private bool _disposed;

    public RaySceneDescriptorBank(
        VulkanContext context,
        AccelerationStructureManager accelerationStructures)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _accelerationStructures = accelerationStructures ??
            throw new ArgumentNullException(nameof(accelerationStructures));
    }

    public DescriptorSetLayout Layout => _layout;
    public bool IsAvailable { get; private set; }
    public string FailureDetail { get; private set; } =
        "ray-scene descriptor bank has not been initialized";

    public bool TryInitialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsAvailable)
            return true;
        if (!_context.RayQuerySupported ||
            _context.KhrAccelerationStructure is null ||
            !_accelerationStructures.Supported)
        {
            FailureDetail =
                "ray-scene descriptors require ray-query and acceleration-structure support";
            return false;
        }

        try
        {
            CreateLayout();
            CreatePoolAndSets();
            IsAvailable = true;
            FailureDetail = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            FailureDetail =
                $"ray-scene descriptor initialization failed: {exception.Message}";
            Cleanup();
            return false;
        }
    }

    public bool TryUpdate(int frameIndex, out string failureDetail)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        failureDetail = string.Empty;
        if (!IsAvailable || (uint)frameIndex >= (uint)_sets.Length)
        {
            failureDetail = !IsAvailable
                ? FailureDetail
                : "ray-scene descriptor frame slot is invalid";
            return false;
        }

        AccelerationStructureKHR tlas =
            _accelerationStructures.TopLevelAccelerationStructureHandle;
        if (tlas.Handle == 0)
        {
            failureDetail = "the shared ray scene has no live TLAS";
            return false;
        }
        if (_boundTlases[frameIndex].Handle == tlas.Handle)
            return true;

        var accelerationStructureInfo =
            new WriteDescriptorSetAccelerationStructureKHR
            {
                SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
                AccelerationStructureCount = 1,
                PAccelerationStructures = &tlas
            };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            PNext = &accelerationStructureInfo,
            DstSet = _sets[frameIndex],
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.AccelerationStructureKhr
        };
        _context.Api.UpdateDescriptorSets(
            _context.Device,
            1,
            &write,
            0,
            null);
        _boundTlases[frameIndex] = tlas;
        return true;
    }

    public void Bind(
        CommandBuffer commandBuffer,
        PipelineBindPoint bindPoint,
        PipelineLayout pipelineLayout,
        int frameIndex)
    {
        if (!TryUpdate(frameIndex, out string failureDetail))
            throw new InvalidOperationException(failureDetail);

        DescriptorSet set = _sets[frameIndex];
        _context.Api.CmdBindDescriptorSets(
            commandBuffer,
            bindPoint,
            pipelineLayout,
            2,
            1,
            &set,
            0,
            null);
    }

    private void CreateLayout()
    {
        var binding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.AccelerationStructureKhr,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.ComputeBit |
                ShaderStageFlags.FragmentBit
        };
        var createInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &binding
        };
        Result result = _context.Api.CreateDescriptorSetLayout(
            _context.Device,
            &createInfo,
            null,
            out _layout);
        if (result != Result.Success)
            throw new VulkanException(
                "Failed to create the shared ray-scene descriptor layout",
                result);
        _context.SetDebugName(
            _layout.Handle,
            ObjectType.DescriptorSetLayout,
            "Shared Ray Scene Descriptor Layout");
    }

    private void CreatePoolAndSets()
    {
        const uint setCount = RenderingConstants.FramesInFlight;
        var poolSize = new DescriptorPoolSize
        {
            Type = DescriptorType.AccelerationStructureKhr,
            DescriptorCount = setCount
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
            MaxSets = setCount
        };
        Result result = _context.Api.CreateDescriptorPool(
            _context.Device,
            &poolInfo,
            null,
            out _pool);
        if (result != Result.Success)
            throw new VulkanException(
                "Failed to create the shared ray-scene descriptor pool",
                result);

        DescriptorSetLayout* layouts =
            stackalloc DescriptorSetLayout[RenderingConstants.FramesInFlight];
        for (int index = 0; index < RenderingConstants.FramesInFlight; index++)
            layouts[index] = _layout;
        fixed (DescriptorSet* sets = _sets)
        {
            var allocateInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _pool,
                DescriptorSetCount = setCount,
                PSetLayouts = layouts
            };
            result = _context.Api.AllocateDescriptorSets(
                _context.Device,
                &allocateInfo,
                sets);
        }
        if (result != Result.Success)
            throw new VulkanException(
                "Failed to allocate shared ray-scene descriptor sets",
                result);
    }

    private void Cleanup()
    {
        IsAvailable = false;
        Array.Clear(_boundTlases);
        Array.Clear(_sets);
        if (_pool.Handle != 0)
            _context.Api.DestroyDescriptorPool(_context.Device, _pool, null);
        if (_layout.Handle != 0)
            _context.Api.DestroyDescriptorSetLayout(
                _context.Device,
                _layout,
                null);
        _pool = default;
        _layout = default;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Cleanup();
        GC.SuppressFinalize(this);
    }
}
