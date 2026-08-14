using System;
using Njulf.Rendering.Core;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline.PipelineObjects;

internal sealed unsafe class DirectionalShadowComputePipeline : IDisposable
{
    private readonly VulkanContext _context;
    private readonly GiPipelineCacheService? _cacheService;
    private readonly string _shaderName;
    private nint _entryPoint;
    private PipelineCache _ownedCache;

    public DirectionalShadowComputePipeline(
        VulkanContext context,
        BindlessHeap bindlessHeap,
        string shaderName,
        uint pushConstantBytes,
        GiPipelineCacheService? cacheService = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _cacheService = cacheService;
        _shaderName = string.IsNullOrWhiteSpace(shaderName)
            ? throw new ArgumentException("Shader name is required.", nameof(shaderName))
            : shaderName;
        PhysicalDeviceProperties properties = default;
        _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice, &properties);
        if (pushConstantBytes > properties.Limits.MaxPushConstantsSize)
        {
            throw new VulkanException(
                $"{shaderName} requires {pushConstantBytes} push-constant bytes, but the device exposes " +
                $"{properties.Limits.MaxPushConstantsSize}.");
        }

        _entryPoint = SilkMarshal.StringToPtr("main");
        DescriptorSetLayout* layouts = stackalloc DescriptorSetLayout[2]
        {
            bindlessHeap.StorageBufferSetLayout,
            bindlessHeap.TextureSamplerSetLayout
        };
        var range = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Size = pushConstantBytes
        };
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 2,
            PSetLayouts = layouts,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &range
        };
        Result result = _context.Api.CreatePipelineLayout(
            _context.Device,
            &layoutInfo,
            null,
            out PipelineLayout layout);
        if (result != Result.Success)
            throw new VulkanException($"Failed to create {shaderName} layout", result);
        Layout = layout;

        PipelineCache cache;
        if (_cacheService != null)
        {
            cache = _cacheService.Cache;
        }
        else
        {
            var cacheInfo = new PipelineCacheCreateInfo
            {
                SType = StructureType.PipelineCacheCreateInfo
            };
            result = _context.Api.CreatePipelineCache(
                _context.Device,
                &cacheInfo,
                null,
                out _ownedCache);
            if (result != Result.Success)
                throw new VulkanException($"Failed to create {shaderName} cache", result);
            cache = _ownedCache;
        }

        ShaderModule module = default;
        try
        {
            module = ShaderModuleLoader.Load(_context, shaderName);
            var stage = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.ComputeBit,
                Module = module,
                PName = (byte*)_entryPoint
            };
            var pipelineInfo = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = stage,
                Layout = Layout,
                BasePipelineIndex = -1
            };
            long start = _cacheService?.BeginPipelineCreation() ?? 0L;
            try
            {
                result = _context.Api.CreateComputePipelines(
                    _context.Device,
                    cache,
                    1,
                    &pipelineInfo,
                    null,
                    out VkPipeline pipeline);
                Pipeline = pipeline;
            }
            finally
            {
                _cacheService?.EndPipelineCreation(
                    $"DirectionalShadow:{shaderName}",
                    start);
            }
            if (result != Result.Success)
                throw new VulkanException($"Failed to create {shaderName} pipeline", result);
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            if (module.Handle != 0)
                _context.Api.DestroyShaderModule(_context.Device, module, null);
        }
    }

    public PipelineLayout Layout { get; private set; }
    public VkPipeline Pipeline { get; private set; }

    public void Dispose()
    {
        if (Pipeline.Handle != 0)
            _context.Api.DestroyPipeline(_context.Device, Pipeline, null);
        if (Layout.Handle != 0)
            _context.Api.DestroyPipelineLayout(_context.Device, Layout, null);
        if (_ownedCache.Handle != 0)
            _context.Api.DestroyPipelineCache(_context.Device, _ownedCache, null);
        if (_entryPoint != 0)
            SilkMarshal.Free(_entryPoint);
        Pipeline = default;
        Layout = default;
        _ownedCache = default;
        _entryPoint = 0;
    }
}
