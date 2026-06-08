using System;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Descriptors
{
    public static class DescriptorSetLayouts
    {
        private static VulkanContext _context;
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
                SType = StructureType.DescriptorSetLayoutBinding,
                Binding = 0,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = (uint)BindlessIndex.MaxTextures,
                StageFlags = ShaderStageFlags.All
            };

            var flags = new DescriptorBindingFlags
            {
                SType = StructureType.DescriptorBindingFlags,
                PBindingFlags = (DescriptorBindingFlagsBit[])Array.Empty<DescriptorBindingFlagsBit>()
            };

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

            if (_context.Vk.CreateDescriptorSetLayout(_context.Device, &createInfo, null, out _bindlessStorageLayout) != Result.Success)
            {
                throw new Exception("Failed to create bindless storage descriptor set layout");
            }
        }

        private static void CreateBindlessTextureLayout()
        {
            var binding = new DescriptorSetLayoutBinding
            {
                SType = StructureType.DescriptorSetLayoutBinding,
                Binding = 0,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = (uint)BindlessIndex.MaxTextures,
                StageFlags = ShaderStageFlags.All
            };

            var flags = new DescriptorBindingFlags
            {
                SType = StructureType.DescriptorBindingFlags,
                PBindingFlags = (DescriptorBindingFlagsBit[])Array.Empty<DescriptorBindingFlagsBit>()
            };

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

            if (_context.Vk.CreateDescriptorSetLayout(_context.Device, &createInfo, null, out _bindlessTextureLayout) != Result.Success)
            {
                throw new Exception("Failed to create bindless texture descriptor set layout");
            }
        }

        public static void Cleanup()
        {
            if (_bindlessStorageLayout != null)
            {
                _context.Vk.DestroyDescriptorSetLayout(_context.Device, _bindlessStorageLayout, null);
                _bindlessStorageLayout = null;
            }
            if (_bindlessTextureLayout != null)
            {
                _context.Vk.DestroyDescriptorSetLayout(_context.Device, _bindlessTextureLayout, null);
                _bindlessTextureLayout = null;
            }
        }
    }
}
