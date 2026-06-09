using System;
using Njulf.Rendering.Core;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Descriptors
{
    public static unsafe class DescriptorSetLayouts
    {
        private static VulkanContext _context = null!;
        private static DescriptorSetLayout _bindlessStorageLayout;
        private static DescriptorSetLayout _bindlessTextureLayout;

        public static DescriptorSetLayout BindlessStorageLayout => _bindlessStorageLayout;
        public static DescriptorSetLayout BindlessTextureLayout => _bindlessTextureLayout;

        public static void Initialize(VulkanContext context)
        {
            _context = context;
            CreateBindlessStorageLayout();
            CreateBindlessTextureLayout();
        }

        private static void CreateBindlessStorageLayout()
        {
            var binding = new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = (uint)BindlessIndex.StaticBufferCount,
                StageFlags = ShaderStageFlags.All
            };

            var flags = DescriptorBindingFlags.UpdateAfterBindBit |
                        DescriptorBindingFlags.PartiallyBoundBit;

            var bindingFlags = new DescriptorSetLayoutBindingFlagsCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
                BindingCount = 1,
                PBindingFlags = &flags
            };

            var createInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 1,
                PBindings = &binding,
                Flags = DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBitExt,
                PNext = &bindingFlags
            };

            if (_context.Api.CreateDescriptorSetLayout(_context.Device, &createInfo, null, out _bindlessStorageLayout) != Result.Success)
            {
                throw new Exception("Failed to create bindless storage descriptor set layout");
            }
        }

        private static void CreateBindlessTextureLayout()
        {
            var binding = new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = (uint)BindlessIndex.MaxTextures,
                StageFlags = ShaderStageFlags.All
            };

            var flags = DescriptorBindingFlags.UpdateAfterBindBit |
                        DescriptorBindingFlags.PartiallyBoundBit;

            var bindingFlags = new DescriptorSetLayoutBindingFlagsCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
                BindingCount = 1,
                PBindingFlags = &flags
            };

            var createInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 1,
                PBindings = &binding,
                Flags = DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBitExt,
                PNext = &bindingFlags
            };

            if (_context.Api.CreateDescriptorSetLayout(_context.Device, &createInfo, null, out _bindlessTextureLayout) != Result.Success)
            {
                throw new Exception("Failed to create bindless texture descriptor set layout");
            }
        }

        public static void Cleanup()
        {
            if (_bindlessStorageLayout.Handle != 0)
            {
                _context.Api.DestroyDescriptorSetLayout(_context.Device, _bindlessStorageLayout, null);
                _bindlessStorageLayout = default;
            }
            if (_bindlessTextureLayout.Handle != 0)
            {
                _context.Api.DestroyDescriptorSetLayout(_context.Device, _bindlessTextureLayout, null);
                _bindlessTextureLayout = default;
            }
        }
    }
}
