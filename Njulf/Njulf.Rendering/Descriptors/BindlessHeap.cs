using System;
using System.Threading;
using Njulf.Rendering.Core;
using Njulf.Rendering.Diagnostics;
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

        // Samplers
        private Sampler _defaultSampler;
        private Sampler _screenSampler;
        private Sampler _hiZSampler;

        // Texture index allocator and durable Vulkan ownership
        private readonly BindlessTextureIndexAllocator _textureIndexAllocator;
        private readonly BindlessHeapRetirementLedger _retirementLedger = new();
        private long _descriptorWriteCount;

        // 0 = active, 1 = disposal started/retryable, 2 = fully disposed.
        private int _lifecycleState;

        private const int MaxStorageBuffers = BindlessIndex.StaticBufferCount + 1024;
        private const int MaxTextures = BindlessIndex.MaxTextures;
        private const ShaderStageFlags BindlessShaderStages =
            ShaderStageFlags.TaskBitExt |
            ShaderStageFlags.MeshBitExt |
            ShaderStageFlags.VertexBit |
            ShaderStageFlags.FragmentBit |
            ShaderStageFlags.ComputeBit;
        private const DescriptorBindingFlags BindlessBindingFlags =
            DescriptorBindingFlags.UpdateAfterBindBit |
            DescriptorBindingFlags.PartiallyBoundBit;

        public BindlessHeap(VulkanContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _textureIndexAllocator = new BindlessTextureIndexAllocator(
                BindlessIndex.FirstDynamicTextureIndex,
                MaxTextures);

            try
            {
                CreateStorageBufferHeap();
                CreateTextureSamplerHeap();
                CreateDefaultSampler();
                CreateScreenSampler();
                CreateHiZSampler();
            }
            catch (Exception constructionFailure)
            {
                Volatile.Write(ref _lifecycleState, 1);
                try
                {
                    _retirementLedger.Retire(DestroyOwnedResource);
                }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException(
                        "Bindless heap construction failed and one or more acquired resources could not be retired.",
                        constructionFailure,
                        cleanupFailure);
                }

                throw;
            }

            System.Diagnostics.Debug.WriteLine("Bindless heap created");
        }

        private void CreateStorageBufferHeap()
        {
            // Create descriptor set layout for storage buffers
            var binding = new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = MaxStorageBuffers,
                StageFlags = BindlessShaderStages,
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
            _retirementLedger.Add(
                BindlessHeapOwnedResource.StorageBufferSetLayout);
            _context.SetDebugName(_storageBufferSetLayout.Handle, ObjectType.DescriptorSetLayout, "Bindless Storage Buffer Set Layout");

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
            _retirementLedger.Add(
                BindlessHeapOwnedResource.StorageBufferPool);
            _context.SetDebugName(_storageBufferPool.Handle, ObjectType.DescriptorPool, "Bindless Storage Buffer Descriptor Pool");

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
            _context.SetDebugName(_storageBufferSet.Handle, ObjectType.DescriptorSet, "Bindless Storage Buffer Descriptor Set");
        }

        private void CreateTextureSamplerHeap()
        {
            // Create descriptor set layout for combined image samplers
            var binding = new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = MaxTextures,
                StageFlags = BindlessShaderStages,
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
            _retirementLedger.Add(
                BindlessHeapOwnedResource.TextureSamplerSetLayout);
            _context.SetDebugName(_textureSamplerSetLayout.Handle, ObjectType.DescriptorSetLayout, "Bindless Texture Sampler Set Layout");

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
            _retirementLedger.Add(
                BindlessHeapOwnedResource.TextureSamplerPool);
            _context.SetDebugName(_textureSamplerPool.Handle, ObjectType.DescriptorPool, "Bindless Texture Sampler Descriptor Pool");

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
            _context.SetDebugName(_textureSamplerSet.Handle, ObjectType.DescriptorSet, "Bindless Texture Sampler Descriptor Set");
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
            _retirementLedger.Add(BindlessHeapOwnedResource.DefaultSampler);
            _context.SetDebugName(_defaultSampler.Handle, ObjectType.Sampler, "Bindless Default Linear Repeat Sampler");
        }

        private void CreateScreenSampler()
        {
            var samplerInfo = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = Filter.Linear,
                MinFilter = Filter.Linear,
                MipmapMode = SamplerMipmapMode.Nearest,
                AddressModeU = SamplerAddressMode.ClampToEdge,
                AddressModeV = SamplerAddressMode.ClampToEdge,
                AddressModeW = SamplerAddressMode.ClampToEdge,
                MipLodBias = 0.0f,
                AnisotropyEnable = false,
                MaxAnisotropy = 1.0f,
                CompareEnable = false,
                CompareOp = CompareOp.Never,
                MinLod = 0.0f,
                MaxLod = 0.0f,
                BorderColor = BorderColor.FloatTransparentBlack,
                UnnormalizedCoordinates = false
            };

            Result result = _context.Api.CreateSampler(
                _context.Device, &samplerInfo, null, out _screenSampler);
            if (result != Result.Success)
                throw new VulkanException("Failed to create screen texture sampler", result);
            _retirementLedger.Add(BindlessHeapOwnedResource.ScreenSampler);
            _context.SetDebugName(_screenSampler.Handle, ObjectType.Sampler, "Bindless Linear Clamp Screen Sampler");
        }

        private void CreateHiZSampler()
        {
            var samplerInfo = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = Filter.Nearest,
                MinFilter = Filter.Nearest,
                MipmapMode = SamplerMipmapMode.Nearest,
                AddressModeU = SamplerAddressMode.ClampToEdge,
                AddressModeV = SamplerAddressMode.ClampToEdge,
                AddressModeW = SamplerAddressMode.ClampToEdge,
                MipLodBias = 0.0f,
                AnisotropyEnable = false,
                MaxAnisotropy = 1.0f,
                CompareEnable = false,
                CompareOp = CompareOp.Never,
                MinLod = 0.0f,
                MaxLod = 16.0f,
                BorderColor = BorderColor.FloatTransparentBlack,
                UnnormalizedCoordinates = false
            };

            Result result = _context.Api.CreateSampler(
                _context.Device, &samplerInfo, null, out _hiZSampler);
            if (result != Result.Success)
                throw new VulkanException("Failed to create Hi-Z texture sampler", result);
            _retirementLedger.Add(BindlessHeapOwnedResource.HiZSampler);
            _context.SetDebugName(_hiZSampler.Handle, ObjectType.Sampler, "Bindless Hi-Z Mip Sampler");
        }

        public DescriptorSet StorageBufferSet
        {
            get
            {
                ThrowIfNotActive();
                return _storageBufferSet;
            }
        }

        public DescriptorSet TextureSamplerSet
        {
            get
            {
                ThrowIfNotActive();
                return _textureSamplerSet;
            }
        }

        public DescriptorSetLayout StorageBufferSetLayout
        {
            get
            {
                ThrowIfNotActive();
                return _storageBufferSetLayout;
            }
        }

        public DescriptorSetLayout TextureSamplerSetLayout
        {
            get
            {
                ThrowIfNotActive();
                return _textureSamplerSetLayout;
            }
        }

        public Sampler DefaultSampler
        {
            get
            {
                ThrowIfNotActive();
                return _defaultSampler;
            }
        }

        public Sampler ScreenSampler
        {
            get
            {
                ThrowIfNotActive();
                return _screenSampler;
            }
        }

        public Sampler HiZSampler
        {
            get
            {
                ThrowIfNotActive();
                return _hiZSampler;
            }
        }

        /// <summary>
        /// Registers a storage buffer at a fixed index.
        /// </summary>
        public void RegisterStorageBuffer(int index, VkBuffer buffer, ulong offset, ulong range)
        {
            ThrowIfNotActive();
            lock (_lock)
            {
                ThrowIfNotActive();
                if (!BindlessIndex.IsStaticBufferIndex(index))
                    throw new ArgumentOutOfRangeException(nameof(index), $"Index must be a static buffer index (0-{BindlessIndex.StaticBufferCount - 1})");

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
                Interlocked.Increment(ref _descriptorWriteCount);
            }
        }

        /// <summary>
        /// Allocates a texture index and registers the texture.
        /// </summary>
        public int AllocateTextureIndex(ImageView view, Sampler sampler = default)
        {
            ThrowIfNotActive();
            lock (_lock)
            {
                ThrowIfNotActive();
                int index = _textureIndexAllocator.GetAllocationCandidate();

                // Descriptor registration is the only fallible step. Reserve
                // allocator state only after it succeeds, so a failed write
                // cannot leak a free-list entry or advance the high-water
                // cursor permanently.
                RegisterTextureLocked(index, view, sampler);
                _textureIndexAllocator.CommitAllocation(index);
                return index;
            }
        }

        /// <summary>
        /// Registers a texture at a specific index.
        /// </summary>
        public void RegisterTexture(
            int index,
            ImageView view,
            Sampler sampler = default,
            ImageLayout imageLayout = ImageLayout.ShaderReadOnlyOptimal)
        {
            ThrowIfNotActive();
            lock (_lock)
            {
                ThrowIfNotActive();
                RegisterTextureLocked(index, view, sampler, imageLayout);
            }
        }

        private void RegisterTextureLocked(
            int index,
            ImageView view,
            Sampler sampler = default,
            ImageLayout imageLayout = ImageLayout.ShaderReadOnlyOptimal)
        {
            if (!BindlessIndex.IsTextureIndex(index))
                throw new ArgumentOutOfRangeException(nameof(index), "Index must be a texture index");
            if (view.Handle == 0)
                throw new ArgumentException("A valid image view is required for a bindless texture descriptor.", nameof(view));

            if (sampler.Handle == 0)
                sampler = _defaultSampler;

            var imageInfo = new DescriptorImageInfo
            {
                Sampler = sampler,
                ImageView = view,
                ImageLayout = imageLayout
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
            Interlocked.Increment(ref _descriptorWriteCount);
        }

        /// <summary>
        /// Returns allocator-backed descriptor pressure for the dynamic combined
        /// image/sampler table. A sampled-image descriptor owns its sampler, so
        /// texture and sampler occupancy are intentionally identical.
        /// </summary>
        public DescriptorPressureSnapshot GetDescriptorPressureSnapshot()
        {
            ThrowIfNotActive();
            lock (_lock)
            {
                ThrowIfNotActive();
                int capacity = _textureIndexAllocator.Capacity;
                int used = _textureIndexAllocator.Used;
                int highWater = _textureIndexAllocator.HighWater;
                int writes = (int)Math.Min(
                    int.MaxValue,
                    Math.Max(0L, Interlocked.Read(ref _descriptorWriteCount)));
                return new DescriptorPressureSnapshot(
                    capacity,
                    used,
                    highWater,
                    capacity,
                    used,
                    highWater,
                    writes);
            }
        }

        /// <summary>
        /// Frees a texture index.
        /// </summary>
        public void FreeTextureIndex(int index)
        {
            ThrowIfNotActive();
            lock (_lock)
            {
                ThrowIfNotActive();
                _textureIndexAllocator.Free(index);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (Volatile.Read(ref _lifecycleState) == 2)
                    return;

                Volatile.Write(ref _lifecycleState, 1);
                _retirementLedger.Retire(DestroyOwnedResource);
                Volatile.Write(ref _lifecycleState, 2);
            }

            GC.SuppressFinalize(this);
            System.Diagnostics.Debug.WriteLine("Bindless heap disposed.");
        }

        private void DestroyOwnedResource(
            BindlessHeapOwnedResource resource)
        {
            switch (resource)
            {
                case BindlessHeapOwnedResource.StorageBufferPool:
                    _context.Api.DestroyDescriptorPool(_context.Device, _storageBufferPool, null);
                    _storageBufferPool = default;
                    _storageBufferSet = default;
                    break;
                case BindlessHeapOwnedResource.StorageBufferSetLayout:
                    _context.Api.DestroyDescriptorSetLayout(_context.Device, _storageBufferSetLayout, null);
                    _storageBufferSetLayout = default;
                    break;
                case BindlessHeapOwnedResource.TextureSamplerPool:
                    _context.Api.DestroyDescriptorPool(_context.Device, _textureSamplerPool, null);
                    _textureSamplerPool = default;
                    _textureSamplerSet = default;
                    break;
                case BindlessHeapOwnedResource.TextureSamplerSetLayout:
                    _context.Api.DestroyDescriptorSetLayout(_context.Device, _textureSamplerSetLayout, null);
                    _textureSamplerSetLayout = default;
                    break;
                case BindlessHeapOwnedResource.DefaultSampler:
                    _context.Api.DestroySampler(_context.Device, _defaultSampler, null);
                    _defaultSampler = default;
                    break;
                case BindlessHeapOwnedResource.ScreenSampler:
                    _context.Api.DestroySampler(_context.Device, _screenSampler, null);
                    _screenSampler = default;
                    break;
                case BindlessHeapOwnedResource.HiZSampler:
                    _context.Api.DestroySampler(_context.Device, _hiZSampler, null);
                    _hiZSampler = default;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(resource),
                        resource,
                        "Unknown bindless resource.");
            }
        }

        private void ThrowIfNotActive()
        {
            if (Volatile.Read(ref _lifecycleState) != 0)
                throw new ObjectDisposedException(nameof(BindlessHeap));
        }

    }

}
