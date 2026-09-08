using Njulf.Shaders;
using NUnit.Framework;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Tests;

[TestFixture]
public sealed unsafe class HiZDepthPyramidGpuTests
{
    [Test]
    [Category("GPU")]
    public void ProductionDownsamplePreservesUncoveredDepthInOddAndThinMipFootprints()
    {
        using var gpu = new DownsampleGpu();
        gpu.Initialize();

        // At 5 -> 2, the center source texel overlaps both destination texels
        // in each axis. A visible hole must therefore reach all four outputs.
        float[] source = Enumerable.Repeat(0.8f, 25).ToArray();
        source[12] = 0f;
        Assert.That(gpu.Run(5, 5, source), Is.EqualTo(new[] { 0f, 0f, 0f, 0f }),
            "A center hole must survive in every overlapping normalized footprint.");

        source[12] = 0.8f;
        source[24] = 0f;
        Assert.That(gpu.Run(5, 5, source), Is.EqualTo(new[] { 0.8f, 0.8f, 0.8f, 0f }),
            "Odd final rows and columns cannot disappear from the pyramid.");
        Assert.That(gpu.Run(3, 1, new[] { 0.8f, 0.8f, 0f }), Is.EqualTo(new[] { 0f }),
            "The last texel must survive even after one axis reaches one texel.");
        Assert.That(gpu.Run(4, 2, new[] { 0.8f, 0.8f, 0.6f, 0.6f, 0.8f, 0.8f, 0.6f, 0.6f }),
            Is.EqualTo(new[] { 0.8f, 0.6f }), "Even reductions retain their existing 2x2 footprint.");
    }

    // Executes the actual production SPIR-V with its sampler/image descriptors
    // and push constants. Linear host-visible images keep this numerical test
    // independent of the renderer's swapchain, bindless heap and scene assets.
    private sealed class DownsampleGpu : IDisposable
    {
        private readonly Vk _vk = Vk.GetApi();
        private Instance _instance;
        private PhysicalDevice _physical;
        private PhysicalDeviceMemoryProperties _memory;
        private Device _device;
        private Queue _queue;
        private CommandPool _pool;
        private DescriptorSetLayout _setLayout;
        private DescriptorPool _descriptorPool;
        private DescriptorSet _set;
        private PipelineLayout _layout;
        private VkPipeline _pipeline;
        private Sampler _sampler;

        public void Initialize()
        {
            var application = new ApplicationInfo { SType = StructureType.ApplicationInfo, ApiVersion = Vk.Version13 };
            var instanceInfo = new InstanceCreateInfo { SType = StructureType.InstanceCreateInfo, PApplicationInfo = &application };
            Check(_vk.CreateInstance(&instanceInfo, null, out _instance));
            uint count = 0;
            Check(_vk.EnumeratePhysicalDevices(_instance, &count, null));
            var devices = new PhysicalDevice[count];
            fixed (PhysicalDevice* pointer = devices)
                Check(_vk.EnumeratePhysicalDevices(_instance, &count, pointer));
            uint family = uint.MaxValue;
            foreach (PhysicalDevice candidate in devices)
            {
                _vk.GetPhysicalDeviceFormatProperties(candidate, Format.R32Sfloat, out FormatProperties format);
                const FormatFeatureFlags required = FormatFeatureFlags.SampledImageBit | FormatFeatureFlags.StorageImageBit;
                if ((format.LinearTilingFeatures & required) != required)
                    continue;
                _vk.GetPhysicalDeviceProperties(candidate, out PhysicalDeviceProperties properties);
                if (properties.ApiVersion < Vk.Version13)
                    continue;
                uint queueCount = 0;
                _vk.GetPhysicalDeviceQueueFamilyProperties(candidate, &queueCount, null);
                var queues = new QueueFamilyProperties[queueCount];
                fixed (QueueFamilyProperties* pointer = queues)
                    _vk.GetPhysicalDeviceQueueFamilyProperties(candidate, &queueCount, pointer);
                for (uint index = 0; index < queueCount; index++)
                {
                    if ((queues[index].QueueFlags & QueueFlags.ComputeBit) == 0)
                        continue;
                    _physical = candidate;
                    family = index;
                    TestContext.Progress.WriteLine($"Hi-Z GPU: {SilkMarshal.PtrToString((nint)properties.DeviceName)}");
                    break;
                }
                if (family != uint.MaxValue)
                    break;
            }
            if (family == uint.MaxValue)
                Assert.Ignore("No Vulkan 1.3 compute device supports linear R32 sampled/storage images.");
            _vk.GetPhysicalDeviceMemoryProperties(_physical, out _memory);
            float priority = 1f;
            var queueInfo = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo, QueueFamilyIndex = family,
                QueueCount = 1, PQueuePriorities = &priority
            };
            var features = new PhysicalDeviceVulkan13Features
            {
                SType = StructureType.PhysicalDeviceVulkan13Features, Maintenance4 = true
            };
            var deviceInfo = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo, PNext = &features,
                QueueCreateInfoCount = 1, PQueueCreateInfos = &queueInfo
            };
            Check(_vk.CreateDevice(_physical, &deviceInfo, null, out _device));
            _vk.GetDeviceQueue(_device, family, 0, out _queue);
            var poolInfo = new CommandPoolCreateInfo { SType = StructureType.CommandPoolCreateInfo, QueueFamilyIndex = family };
            Check(_vk.CreateCommandPool(_device, &poolInfo, null, out _pool));
            var samplerInfo = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo, MinFilter = Filter.Nearest, MagFilter = Filter.Nearest,
                MipmapMode = SamplerMipmapMode.Nearest,
                AddressModeU = SamplerAddressMode.ClampToEdge, AddressModeV = SamplerAddressMode.ClampToEdge,
                AddressModeW = SamplerAddressMode.ClampToEdge
            };
            Check(_vk.CreateSampler(_device, &samplerInfo, null, out _sampler));
            DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[2];
            bindings[0] = new(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit);
            bindings[1] = new(1, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
            var setInfo = new DescriptorSetLayoutCreateInfo
            { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 2, PBindings = bindings };
            Check(_vk.CreateDescriptorSetLayout(_device, &setInfo, null, out _setLayout));
            DescriptorPoolSize* sizes = stackalloc DescriptorPoolSize[2];
            sizes[0] = new(DescriptorType.CombinedImageSampler, 1);
            sizes[1] = new(DescriptorType.StorageImage, 1);
            var descriptorInfo = new DescriptorPoolCreateInfo
            { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 1, PoolSizeCount = 2, PPoolSizes = sizes };
            Check(_vk.CreateDescriptorPool(_device, &descriptorInfo, null, out _descriptorPool));
            DescriptorSetLayout setLayout = _setLayout;
            var allocateSet = new DescriptorSetAllocateInfo
            { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _descriptorPool, DescriptorSetCount = 1, PSetLayouts = &setLayout };
            Check(_vk.AllocateDescriptorSets(_device, &allocateSet, out _set));
            var range = new PushConstantRange(ShaderStageFlags.ComputeBit, 0, 16);
            var layoutInfo = new PipelineLayoutCreateInfo
            { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &setLayout, PushConstantRangeCount = 1, PPushConstantRanges = &range };
            Check(_vk.CreatePipelineLayout(_device, &layoutInfo, null, out _layout));

            // Optional one-run baseline bytecode permits failure sensitivity
            // checks without reverting production source or replacing its DLL.
            string? baselineShader = Environment.GetEnvironmentVariable("NJULF_HIZ_TEST_SHADER");
            using Stream shader = string.IsNullOrEmpty(baselineShader)
                ? typeof(ShaderLibrary).Assembly.GetManifestResourceStream("Njulf.Shaders.hiz_downsample.comp")!
                : File.OpenRead(baselineShader);
            Assert.That(shader, Is.Not.Null, "Production Hi-Z shader resource is missing.");
            using var bytes = new MemoryStream();
            shader.CopyTo(bytes);
            fixed (byte* code = bytes.ToArray())
            {
                var moduleInfo = new ShaderModuleCreateInfo
                { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)bytes.Length, PCode = (uint*)code };
                Check(_vk.CreateShaderModule(_device, &moduleInfo, null, out ShaderModule module));
                try
                {
                    byte* entry = stackalloc byte[] { (byte)'m', (byte)'a', (byte)'i', (byte)'n', 0 };
                    var pipelineInfo = new ComputePipelineCreateInfo
                    {
                        SType = StructureType.ComputePipelineCreateInfo, Layout = _layout, BasePipelineIndex = -1,
                        Stage = new PipelineShaderStageCreateInfo
                        { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ComputeBit, Module = module, PName = entry }
                    };
                    Check(_vk.CreateComputePipelines(_device, default, 1, &pipelineInfo, null, out _pipeline));
                }
                finally { _vk.DestroyShaderModule(_device, module, null); }
            }
        }

        public float[] Run(uint width, uint height, float[] values)
        {
            uint dstWidth = Math.Max(1, width / 2), dstHeight = Math.Max(1, height / 2);
            using var source = new HostImage(this, width, height, ImageUsageFlags.SampledBit);
            using var destination = new HostImage(this, dstWidth, dstHeight, ImageUsageFlags.StorageBit);
            source.Write(values);
            DescriptorImageInfo sourceInfo = new(_sampler, source.View, ImageLayout.General);
            DescriptorImageInfo destinationInfo = new(default, destination.View, ImageLayout.General);
            WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[2];
            writes[0] = new WriteDescriptorSet
            { SType = StructureType.WriteDescriptorSet, DstSet = _set, DstBinding = 0, DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &sourceInfo };
            writes[1] = new WriteDescriptorSet
            { SType = StructureType.WriteDescriptorSet, DstSet = _set, DstBinding = 1, DescriptorCount = 1, DescriptorType = DescriptorType.StorageImage, PImageInfo = &destinationInfo };
            _vk.UpdateDescriptorSets(_device, 2, writes, 0, null);
            var allocate = new CommandBufferAllocateInfo
            { SType = StructureType.CommandBufferAllocateInfo, CommandPool = _pool, Level = CommandBufferLevel.Primary, CommandBufferCount = 1 };
            CommandBuffer command;
            Check(_vk.AllocateCommandBuffers(_device, &allocate, &command));
            try
            {
                var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
                Check(_vk.BeginCommandBuffer(command, &begin));
                ImageMemoryBarrier* barriers = stackalloc ImageMemoryBarrier[2];
                barriers[0] = Barrier(source.Image, AccessFlags.ShaderReadBit);
                barriers[1] = Barrier(destination.Image, AccessFlags.ShaderWriteBit);
                _vk.CmdPipelineBarrier(command, PipelineStageFlags.HostBit, PipelineStageFlags.ComputeShaderBit,
                    0, 0, null, 0, null, 2, barriers);
                _vk.CmdBindPipeline(command, PipelineBindPoint.Compute, _pipeline);
                DescriptorSet set = _set;
                _vk.CmdBindDescriptorSets(command, PipelineBindPoint.Compute, _layout, 0, 1, &set, 0, null);
                float* dimensions = stackalloc float[] { width, height, dstWidth, dstHeight };
                _vk.CmdPushConstants(command, _layout, ShaderStageFlags.ComputeBit, 0, 16, dimensions);
                _vk.CmdDispatch(command, (dstWidth + 7) / 8, (dstHeight + 7) / 8, 1);
                var readback = new MemoryBarrier
                { SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.ShaderWriteBit, DstAccessMask = AccessFlags.HostReadBit };
                _vk.CmdPipelineBarrier(command, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.HostBit,
                    0, 1, &readback, 0, null, 0, null);
                Check(_vk.EndCommandBuffer(command));
                var submit = new SubmitInfo
                { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &command };
                Check(_vk.QueueSubmit(_queue, 1, &submit, default));
                Check(_vk.QueueWaitIdle(_queue));
                return destination.Read();
            }
            finally { _vk.FreeCommandBuffers(_device, _pool, 1, &command); }
        }

        private static ImageMemoryBarrier Barrier(Image image, AccessFlags access) => new()
        {
            SType = StructureType.ImageMemoryBarrier, Image = image,
            OldLayout = ImageLayout.Preinitialized, NewLayout = ImageLayout.General,
            SrcAccessMask = AccessFlags.HostWriteBit, DstAccessMask = access,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            SubresourceRange = new(ImageAspectFlags.ColorBit, 0, 1, 0, 1)
        };

        public void Dispose()
        {
            if (_device.Handle != 0)
            {
                _vk.DeviceWaitIdle(_device);
                if (_pipeline.Handle != 0) _vk.DestroyPipeline(_device, _pipeline, null);
                if (_layout.Handle != 0) _vk.DestroyPipelineLayout(_device, _layout, null);
                if (_descriptorPool.Handle != 0) _vk.DestroyDescriptorPool(_device, _descriptorPool, null);
                if (_setLayout.Handle != 0) _vk.DestroyDescriptorSetLayout(_device, _setLayout, null);
                if (_sampler.Handle != 0) _vk.DestroySampler(_device, _sampler, null);
                if (_pool.Handle != 0) _vk.DestroyCommandPool(_device, _pool, null);
                _vk.DestroyDevice(_device, null);
            }
            if (_instance.Handle != 0) _vk.DestroyInstance(_instance, null);
            _vk.Dispose();
        }

        private sealed class HostImage : IDisposable
        {
            private readonly DownsampleGpu _gpu;
            private readonly uint _width, _height;
            private DeviceMemory _allocation;
            private SubresourceLayout _subresource;
            public Image Image;
            public ImageView View;

            public HostImage(DownsampleGpu gpu, uint width, uint height, ImageUsageFlags usage)
            {
                _gpu = gpu; _width = width; _height = height;
                try
                {
                    var info = new ImageCreateInfo
                    {
                        SType = StructureType.ImageCreateInfo, ImageType = ImageType.Type2D,
                        Format = Format.R32Sfloat, Extent = new(width, height, 1), MipLevels = 1, ArrayLayers = 1,
                        Samples = SampleCountFlags.Count1Bit, Tiling = ImageTiling.Linear,
                        Usage = usage, InitialLayout = ImageLayout.Preinitialized, SharingMode = SharingMode.Exclusive
                    };
                    Check(gpu._vk.CreateImage(gpu._device, &info, null, out Image));
                    gpu._vk.GetImageMemoryRequirements(gpu._device, Image, out MemoryRequirements requirements);
                    uint memoryType = uint.MaxValue;
                    const MemoryPropertyFlags required = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
                    for (uint index = 0; index < gpu._memory.MemoryTypeCount; index++)
                        if ((requirements.MemoryTypeBits & (1u << (int)index)) != 0 &&
                            (gpu._memory.MemoryTypes[(int)index].PropertyFlags & required) == required)
                        { memoryType = index; break; }
                    if (memoryType == uint.MaxValue)
                        Assert.Ignore("Linear R32 images have no host-visible coherent memory type.");
                    var allocate = new MemoryAllocateInfo
                    { SType = StructureType.MemoryAllocateInfo, AllocationSize = requirements.Size, MemoryTypeIndex = memoryType };
                    Check(gpu._vk.AllocateMemory(gpu._device, &allocate, null, out _allocation));
                    Check(gpu._vk.BindImageMemory(gpu._device, Image, _allocation, 0));
                    var view = new ImageViewCreateInfo
                    { SType = StructureType.ImageViewCreateInfo, Image = Image, ViewType = ImageViewType.Type2D, Format = Format.R32Sfloat, SubresourceRange = new(ImageAspectFlags.ColorBit, 0, 1, 0, 1) };
                    Check(gpu._vk.CreateImageView(gpu._device, &view, null, out View));
                    var subresource = new ImageSubresource(ImageAspectFlags.ColorBit, 0, 0);
                    gpu._vk.GetImageSubresourceLayout(gpu._device, Image, &subresource, out _subresource);
                }
                catch { Dispose(); throw; }
            }

            public void Write(float[] values) => Copy(values, true);
            public float[] Read() { var values = new float[_width * _height]; Copy(values, false); return values; }
            private void Copy(float[] values, bool write)
            {
                void* pointer;
                Check(_gpu._vk.MapMemory(_gpu._device, _allocation, 0, Vk.WholeSize, 0, &pointer));
                try
                {
                    for (int y = 0; y < _height; y++)
                    {
                        var row = new Span<float>((byte*)pointer + _subresource.Offset + (ulong)y * _subresource.RowPitch, (int)_width);
                        Span<float> data = values.AsSpan(y * (int)_width, (int)_width);
                        if (write) data.CopyTo(row); else row.CopyTo(data);
                    }
                }
                finally { _gpu._vk.UnmapMemory(_gpu._device, _allocation); }
            }
            public void Dispose()
            {
                if (View.Handle != 0) _gpu._vk.DestroyImageView(_gpu._device, View, null);
                if (Image.Handle != 0) _gpu._vk.DestroyImage(_gpu._device, Image, null);
                if (_allocation.Handle != 0) _gpu._vk.FreeMemory(_gpu._device, _allocation, null);
            }
        }

        private static void Check(Result result) => Assert.That(result, Is.EqualTo(Result.Success), "Vulkan Hi-Z dispatch failed.");
    }
}
