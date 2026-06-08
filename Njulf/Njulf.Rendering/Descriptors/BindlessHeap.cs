using System;
using System.Collections.Generic;
using Njulf.Rendering.Core;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Descriptors
{
    /// <summary>
    /// Manages bindless descriptor heaps for storage buffers and combined image samplers.
    /// Uses two large heaps with single binding, update-after-bind, variable descriptor count.
    /// </summary>
    public sealed unsafe class BindlessHeap : IDisposable
    {
        private readonly VulkanContext _context;
        private readonly object _lock = new object();
        
        // Storage buffer heap
        private DescriptorPool _storageBufferPool;
        private DescriptorSetLayout _storageBufferSetLayout;
        private DescriptorSet _storageBufferSet;
        
        // Texture heap
        private DescriptorPool _textureSamplerPool;
        private DescriptorSetLayout _textureSamplerSetLayout;
        private DescriptorSet _textureSamplerSet;
        
        // Default sampler
        private Sampler _defaultSampler;
        
        // Texture index allocator
        private readonly Stack<int> _freeTextureIndices = new Stack<int>();
        private int _nextTextureIndex;
        
        private bool _disposed;
        
        private const int MaxStorageBuffers = BindlessIndex.StaticBufferCount + 1024;
        private const int MaxTextures = BindlessIndex.MaxTextures;
        private const DescriptorBindingFlags BindlessBindingFlags =
            DescriptorBindingFlags.UpdateAfterBindBit |
            DescriptorBindingFlags.PartiallyBoundBit;
        
        public BindlessHeap(VulkanContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            
            CreateStorageBufferHeap();
            CreateTextureSamplerHeap();
            CreateDefaultSampler();
            
            _nextTextureIndex = BindlessIndex.FirstTextureIndex;
            
            Console.WriteLine("Bindless heap created");
        }
        
        private void CreateStorageBufferHeap()
        {
            // Create descriptor set layout for storage buffers
            var binding = new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = MaxStorageBuffers,
                StageFlags = ShaderStageFlags.AllGraphics | ShaderStageFlags.ComputeBit,
                PImmutableSamplers = null
            };

            var bindingFlags = BindlessBindingFlags;
            var layoutBindingFlags = new DescriptorSetLayoutBindingFlagsCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
                BindingCount = 1,
                PBindingFlags = &bindingFlags
            };
            
            var layoutInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                PNext = &layoutBindingFlags,
                BindingCount = 1,
                PBindings = &binding,
                Flags = DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBitExt
            };
            
            Result result = _context.Api.CreateDescriptorSetLayout(
                _context.Device, &layoutInfo, null, out _storageBufferSetLayout);
            if (result != Result.Success)
                throw new VulkanException("Failed to create storage buffer descriptor set layout", result);
            
            // Create descriptor pool for storage buffers
            var poolSize = new DescriptorPoolSize
            {
                Type = DescriptorType.StorageBuffer,
                DescriptorCount = MaxStorageBuffers
            };
            
            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                PoolSizeCount = 1,
                PPoolSizes = &poolSize,
                MaxSets = 1,
                Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit |
                        DescriptorPoolCreateFlags.UpdateAfterBindBitExt
            };
            
            result = _context.Api.CreateDescriptorPool(
                _context.Device, &poolInfo, null, out _storageBufferPool);
            if (result != Result.Success)
                throw new VulkanException("Failed to create storage buffer descriptor pool", result);
            
            // Allocate descriptor set
            var storageLayout = _storageBufferSetLayout;
            var allocInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _storageBufferPool,
                DescriptorSetCount = 1,
                PSetLayouts = &storageLayout
            };
            
            result = _context.Api.AllocateDescriptorSets(
                _context.Device, &allocInfo, out _storageBufferSet);
            if (result != Result.Success)
                throw new VulkanException("Failed to allocate storage buffer descriptor set", result);
        }
        
        private void CreateTextureSamplerHeap()
        {
            // Create descriptor set layout for combined image samplers
            var binding = new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = MaxTextures,
                StageFlags = ShaderStageFlags.AllGraphics | ShaderStageFlags.ComputeBit,
                PImmutableSamplers = null
            };

            var bindingFlags = BindlessBindingFlags;
            var layoutBindingFlags = new DescriptorSetLayoutBindingFlagsCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
                BindingCount = 1,
                PBindingFlags = &bindingFlags
            };
            
            var layoutInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                PNext = &layoutBindingFlags,
                BindingCount = 1,
                PBindings = &binding,
                Flags = DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBitExt
            };
            
            Result result = _context.Api.CreateDescriptorSetLayout(
                _context.Device, &layoutInfo, null, out _textureSamplerSetLayout);
            if (result != Result.Success)
                throw new VulkanException("Failed to create texture sampler descriptor set layout", result);
            
            // Create descriptor pool for textures
            var poolSize = new DescriptorPoolSize
            {
                Type = DescriptorType.CombinedImageSampler,
                DescriptorCount = MaxTextures
            };
            
            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                PoolSizeCount = 1,
                PPoolSizes = &poolSize,
                MaxSets = 1,
                Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit |
                        DescriptorPoolCreateFlags.UpdateAfterBindBitExt
            };
            
            result = _context.Api.CreateDescriptorPool(
                _context.Device, &poolInfo, null, out _textureSamplerPool);
            if (result != Result.Success)
                throw new VulkanException("Failed to create texture sampler descriptor pool", result);
            
            // Allocate descriptor set
            var textureLayout = _textureSamplerSetLayout;
            var allocInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _textureSamplerPool,
                DescriptorSetCount = 1,
                PSetLayouts = &textureLayout
            };
            
            result = _context.Api.AllocateDescriptorSets(
                _context.Device, &allocInfo, out _textureSamplerSet);
            if (result != Result.Success)
                throw new VulkanException("Failed to allocate texture sampler descriptor set", result);
        }
        
        private void CreateDefaultSampler()
        {
            var samplerInfo = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = Filter.Linear,
                MinFilter = Filter.Linear,
                MipmapMode = SamplerMipmapMode.Linear,
                AddressModeU = SamplerAddressMode.Repeat,
                AddressModeV = SamplerAddressMode.Repeat,
                AddressModeW = SamplerAddressMode.Repeat,
                MipLodBias = 0.0f,
                AnisotropyEnable = true,
                MaxAnisotropy = 16.0f,
                CompareEnable = false,
                CompareOp = CompareOp.Never,
                MinLod = 0.0f,
                MaxLod = 16.0f,
                BorderColor = BorderColor.FloatTransparentBlack,
                UnnormalizedCoordinates = false
            };
            
            Result result = _context.Api.CreateSampler(
                _context.Device, &samplerInfo, null, out _defaultSampler);
            if (result != Result.Success)
                throw new VulkanException("Failed to create default sampler", result);
        }
        
        public DescriptorSet StorageBufferSet => _storageBufferSet;
        public DescriptorSet TextureSamplerSet => _textureSamplerSet;
        public DescriptorSetLayout StorageBufferSetLayout => _storageBufferSetLayout;
        public DescriptorSetLayout TextureSamplerSetLayout => _textureSamplerSetLayout;
        public Sampler DefaultSampler => _defaultSampler;
        
        /// <summary>
        /// Registers a storage buffer at a fixed index.
        /// </summary>
        public void RegisterStorageBuffer(int index, VkBuffer buffer, ulong offset, ulong range)
        {
            if (!BindlessIndex.IsStaticBufferIndex(index))
                throw new ArgumentOutOfRangeException(nameof(index), "Index must be a static buffer index (0-14)");
            
            var bufferInfo = new DescriptorBufferInfo
            {
                Buffer = buffer,
                Offset = offset,
                Range = range
            };

            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _storageBufferSet,
                DstBinding = 0,
                DstArrayElement = (uint)index,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &bufferInfo
            };
            
            _context.Api.UpdateDescriptorSets(_context.Device, 1, &write, 0, null);
        }
        
        /// <summary>
        /// Allocates a texture index and registers the texture.
        /// </summary>
        public int AllocateTextureIndex(ImageView view, Sampler sampler = default)
        {
            lock (_lock)
            {
                int index;
                if (_freeTextureIndices.Count > 0)
                {
                    index = _freeTextureIndices.Pop();
                }
                else
                {
                    index = _nextTextureIndex++;
                    if (index >= MaxTextures)
                        throw new InvalidOperationException("Max texture count reached");
                }
                
                RegisterTexture(index, view, sampler);
                return index;
            }
        }
        
        /// <summary>
        /// Registers a texture at a specific index.
        /// </summary>
        public void RegisterTexture(int index, ImageView view, Sampler sampler = default)
        {
            if (!BindlessIndex.IsTextureIndex(index))
                throw new ArgumentOutOfRangeException(nameof(index), "Index must be a texture index");
            
            if (sampler.Handle == 0)
                sampler = _defaultSampler;
            
            var imageInfo = new DescriptorImageInfo
            {
                Sampler = sampler,
                ImageView = view,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal
            };

            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _textureSamplerSet,
                DstBinding = 0,
                DstArrayElement = (uint)(index - BindlessIndex.FirstTextureIndex),
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &imageInfo
            };
            
            _context.Api.UpdateDescriptorSets(_context.Device, 1, &write, 0, null);
        }
        
        /// <summary>
        /// Frees a texture index.
        /// </summary>
        public void FreeTextureIndex(int index)
        {
            if (!BindlessIndex.IsTextureIndex(index))
                return;
            
            lock (_lock)
            {
                _freeTextureIndices.Push(index);
            }
        }
        
        /// <summary>
        /// Registers all static buffers. Call this after all static buffers are created.
        /// </summary>
        public void RegisterStaticBuffers()
        {
            // Static buffers 0-14 are pre-registered in the shader
            // This method would be called to update them with actual buffer handles
        }
        
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        
        private void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            
            lock (_lock)
            {
                if (_storageBufferPool.Handle != 0)
                    _context.Api.DestroyDescriptorPool(_context.Device, _storageBufferPool, null);
                
                if (_storageBufferSetLayout.Handle != 0)
                    _context.Api.DestroyDescriptorSetLayout(_context.Device, _storageBufferSetLayout, null);
                
                if (_textureSamplerPool.Handle != 0)
                    _context.Api.DestroyDescriptorPool(_context.Device, _textureSamplerPool, null);
                
                if (_textureSamplerSetLayout.Handle != 0)
                    _context.Api.DestroyDescriptorSetLayout(_context.Device, _textureSamplerSetLayout, null);
                
                if (_defaultSampler.Handle != 0)
                    _context.Api.DestroySampler(_context.Device, _defaultSampler, null);
            }
            
            Console.WriteLine("Bindless heap disposed.");
        }
        
        ~BindlessHeap()
        {
            Dispose(false);
        }
    }
    
}
