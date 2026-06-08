using System;
using Silk.NET.Core.Native;
using Njulf.Rendering.Core;
using Njulf.Rendering.Descriptors;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline.PipelineObjects
{
    public sealed unsafe class ComputePipeline : IDisposable
    {
        private readonly VulkanContext _context;
        private readonly BindlessHeap _bindlessHeap;
        private readonly nint _entryPointName;
        private VkPipeline _pipeline;
        private PipelineLayout _layout;
        private bool _disposed;
        
        public ComputePipeline(VulkanContext context, BindlessHeap bindlessHeap)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bindlessHeap = bindlessHeap ?? throw new ArgumentNullException(nameof(bindlessHeap));
            _entryPointName = SilkMarshal.StringToPtr("main");
            CreatePipeline();
        }
        
        private void CreatePipeline()
        {
            // Create pipeline layout
            var layoutInfo = CreatePipelineLayout();
            
            Result result = _context.Api.CreatePipelineLayout(
                _context.Device, &layoutInfo, null, out _layout);
            if (result != Result.Success)
                throw new VulkanException("Failed to create compute pipeline layout", result);
            
            // Create compute pipeline
            var pipelineInfo = CreateComputePipelineInfo(_layout);
            
            result = _context.Api.CreateComputePipelines(
                _context.Device, default, 1, &pipelineInfo, null, out _pipeline);
            if (result != Result.Success)
                throw new VulkanException("Failed to create compute pipeline", result);
            
            Console.WriteLine("Compute pipeline created.");
        }
        
        private PipelineLayoutCreateInfo CreatePipelineLayout()
        {
            var storageBufferLayout = _bindlessHeap.StorageBufferSetLayout;
            var textureSamplerLayout = _bindlessHeap.TextureSamplerSetLayout;
            
            var setLayouts = stackalloc DescriptorSetLayout[2];
            setLayouts[0] = storageBufferLayout;
            setLayouts[1] = textureSamplerLayout;
            
            var pushConstantRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.ComputeBit,
                Offset = 0,
                Size = 256
            };
            
            return new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 2,
                PSetLayouts = setLayouts,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushConstantRange
            };
        }
        
        private ComputePipelineCreateInfo CreateComputePipelineInfo(PipelineLayout layout)
        {
            var shaderStageInfo = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.ComputeBit,
                Module = default, // TODO: Load actual shader module
                PName = (byte*)_entryPointName,
                PSpecializationInfo = null
            };
            
            return new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = shaderStageInfo,
                Layout = layout,
                BasePipelineHandle = default,
                BasePipelineIndex = 0
            };
        }
        
        public VkPipeline Pipeline => _pipeline;
        public PipelineLayout Layout => _layout;
        
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        
        private void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            
            if (_pipeline.Handle != 0)
                _context.Api.DestroyPipeline(_context.Device, _pipeline, null);
            
            if (_layout.Handle != 0)
                _context.Api.DestroyPipelineLayout(_context.Device, _layout, null);

            if (_entryPointName != 0)
                SilkMarshal.Free(_entryPointName);
            
            Console.WriteLine("Compute pipeline disposed.");
        }
        
        ~ComputePipeline()
        {
            Dispose(false);
        }
    }
}
